"""A2 offline check of the prop GLBs the plugin draws.

The static loader used to read `meshes[0].primitives[0]` and `materials[0]`, so a
client prop — whose shapes are split one material each — was drawn as one part
out of seven or nine. It now merges every primitive into one mesh with one
submesh per primitive and one colour map each; this script proves the shipped
files carry what that merge expects, and prints the numbers the runtime logs so a
live line can be compared with them.

The prop builder writes **one vertex buffer for the whole model** and gives every
primitive its own index accessor into it, so the merge re-adds that buffer per
primitive (the merged count is shared vertices × primitives) and offsets each
primitive's indices by its own base. The index range of every primitive is read
back here, which is the one thing that would silently break the merge.

  python mod/tools/data/check-props.py            # every shipped prop
  python mod/tools/data/check-props.py --all      # also the unshipped ones
"""

import argparse
import json
import math
import pathlib
import struct
import sys

MOD = pathlib.Path(__file__).resolve().parents[2]
SHIPPED = MOD / "Wraeclast/assets/models/props"
BUILT = MOD / "art/game/models/props"

GLB_MAGIC = 0x46546C67
CHUNK_JSON = 0x4E4F534A
CHUNK_BIN = 0x004E4942

USHORT = 5123
UINT = 5125


def load(path):
    blob = path.read_bytes()
    if struct.unpack_from("<I", blob, 0)[0] != GLB_MAGIC:
        raise SystemExit(f"{path.name} is not a GLB")

    document = binary = None
    offset = 12
    while offset + 8 <= len(blob):
        length, kind = struct.unpack_from("<II", blob, offset)
        offset += 8
        if kind == CHUNK_JSON:
            document = json.loads(blob[offset:offset + length])
        elif kind == CHUNK_BIN:
            binary = blob[offset:offset + length]
        offset += length

    if document is None:
        raise SystemExit(f"{path.name} has no JSON chunk")
    return document, binary


def accessor_start(document, accessor):
    view = document["bufferViews"][accessor["bufferView"]]
    return view.get("byteOffset", 0) + accessor.get("byteOffset", 0)


def max_index(document, binary, accessor_index):
    accessor = document["accessors"][accessor_index]
    component = accessor["componentType"]
    if component not in (USHORT, UINT):
        raise SystemExit(f"unsupported index component {component}")
    fmt = "<H" if component == USHORT else "<I"
    step = struct.calcsize(fmt)
    start = accessor_start(document, accessor)

    highest = 0
    for index in range(accessor["count"]):
        value = struct.unpack_from(fmt, binary, start + index * step)[0]
        highest = max(highest, value)
    return highest


def rotated_bounds(low, high, quaternion):
    """The box the prop occupies once the GLB's root rotation is applied."""
    x, y, z, w = quaternion
    matrix = (
        (1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
        (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
        (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)),
    )

    result_low = [math.inf] * 3
    result_high = [-math.inf] * 3
    for corner in range(8):
        local = [high[axis] if (corner >> axis) & 1 else low[axis] for axis in range(3)]
        for row in range(3):
            value = sum(matrix[row][column] * local[column] for column in range(3))
            result_low[row] = min(result_low[row], value)
            result_high[row] = max(result_high[row], value)
    return result_low, result_high


def check(path):
    document, binary = load(path)
    root = document["nodes"][document["scenes"][0]["nodes"][0]]
    rotation = root.get("rotation", [0.0, 0.0, 0.0, 1.0])
    primitives = document["meshes"][0]["primitives"]
    materials = document.get("materials", [])

    low = [math.inf] * 3
    high = [-math.inf] * 3
    merged_vertices = 0
    triangles = 0
    textured = 0
    failures = []

    for index, primitive in enumerate(primitives):
        position_index = primitive["attributes"].get("POSITION")
        if position_index is None:
            failures.append(f"primitive {index} has no POSITION")
            continue

        position = document["accessors"][position_index]
        merged_vertices += position["count"]

        index_accessor = document["accessors"][primitive["indices"]]
        triangles += index_accessor["count"] // 3
        if index_accessor["count"] % 3 != 0:
            failures.append(f"primitive {index} is not a triangle list")

        highest = max_index(document, binary, primitive["indices"])
        if highest >= position["count"]:
            failures.append(
                f"primitive {index} indexes vertex {highest} of {position['count']}, "
                "which the merge cannot offset"
            )

        for axis in range(3):
            low[axis] = min(low[axis], position["min"][axis])
            high[axis] = max(high[axis], position["max"][axis])

        material = materials[primitive.get("material", 0)]
        if "baseColorTexture" in material.get("pbrMetallicRoughness", {}):
            textured += 1
        else:
            textured += 0

    size = [high[axis] - low[axis] for axis in range(3)]
    runtime_low, runtime_high = rotated_bounds(low, high, rotation)
    runtime = [runtime_high[axis] - runtime_low[axis] for axis in range(3)]

    print(
        f"{path.name:24s} submeshes {len(primitives)}, materials {len(materials)}, "
        f"textured {textured}, merged verts {merged_vertices}, tris {triangles}"
    )
    print(
        f"{'':24s} authored {size[0]:5.2f}x{size[1]:5.2f}x{size[2]:5.2f} m -> "
        f"runtime {runtime[0]:5.2f}x{runtime[1]:5.2f}x{runtime[2]:5.2f} m, "
        f"base y {runtime_low[1]:6.2f}"
    )

    if len(primitives) > 1 and textured == 0:
        failures.append("no primitive carries a colour map")
    if any(value <= 0 for value in runtime):
        failures.append("the rotated box is degenerate")

    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--all", action="store_true", help="also check the unshipped props")
    arguments = parser.parse_args()

    folder = BUILT if arguments.all else SHIPPED
    files = sorted(folder.glob("*.glb"))
    if not files:
        print(f"no GLB under {folder} (run node mod/tools/poe-gltf/build-glb.mjs --props)")
        return 1

    failures = []
    for path in files:
        for failure in check(path):
            failures.append(f"{path.name}: {failure}")

    for failure in failures:
        print(f"FAIL {failure}")
    print(f"{len(files)} prop(s) checked, {len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
