"""Build the item donor table for the EItem port.

Items are unlike tomes: `ItemData` carries only presentation (name, text, icon,
rarity, price, pool flags) and the effect lives in code, one
`ItemImplementations.Item*` class per `EItem`, dispatched by `ItemFactory`.
Renaming a slot therefore renames the *donor* effect, so a PoE relic may only
take a slot whose native effect its text can honestly describe.

This script reads the reference data and the IL2CPP ISIL dump and writes
`docs/ITEM_DONORS.md`: one row per EItem with the native text, the stat
modifiers the class writes through `ItemBase.SetStat`, and the non-trivial
code hooks (procs, ticks, kill handlers). It is the donor list for the relic
mapping step; it does not modify the plugin.

Usage:
    python mod/tools/data/dump-item-donors.py            # write the doc
    python mod/tools/data/dump-item-donors.py --check    # fail if the doc is stale
"""

import argparse
import json
import pathlib
import re
import struct
import sys

MOD = pathlib.Path(__file__).resolve().parents[2]
ROOT = MOD.parent
ITEMS_JSON = MOD / "design/megabonk/items.json"
ENUMS_JSON = MOD / "design/megabonk/enums.json"
FACTORY = (
    ROOT
    / "lab/unitypy/cpp2il_isil2/IsilDump/Assembly-CSharp/Assets/Scripts"
    / "Inventory__Items__Pickups/Items/ItemFactory.txt"
)
IMPL_DIR = (
    ROOT
    / "lab/unitypy/cpp2il_isil2/IsilDump/Assembly-CSharp/Assets/Scripts"
    / "Inventory__Items__Pickups/Items/ItemImplementations"
)
OUT = ROOT / "lab/backup/docs/ITEM_DONORS.md"

# EItem members whose factory branch is shared with an earlier class (the
# jump-table arm is a bare Goto, so no `typeof` line of its own).
SHARED_SLOTS = {
    "GoldenRing": "ItemNoImplementation",  # "It doesn't seem to do anything..."
    "CryptKey": "ItemKey",
}

# Methods that only exist as the ItemBase no-op overrides. Anything else is
# listed as a hook so the mapping step can see where the effect lives.
NOOP_METHODS = {
    ".ctor",
    ".cctor",
}
# Methods that describe the card or gate a hook, not the effect itself.
PRESENTATION_METHODS = {
    "GetDescription",
    "GetLocalizationKeys",
    "HasPreAttackProc",
    "HasOnHitEffectProc",
    "OnInitOrAmountChanged",
}


def read_text(path):
    return path.read_text(encoding="utf-8", errors="replace")


def split_methods(text):
    """Return {signature: [isil instructions]} for one ISIL dump."""
    methods = {}
    current = None
    in_isil = False
    for line in text.splitlines():
        if line.startswith("Method: "):
            current = line[len("Method: "):].strip()
            methods[current] = []
            in_isil = False
            continue
        if line.strip() == "ISIL:":
            in_isil = True
            continue
        if current is None or not in_isil:
            continue
        match = re.match(r"\t\d+ (.+)$", line)
        if match:
            methods[current].append(match.group(1).strip())
    return methods


def float_from_hex(value):
    bits = int(value, 16) & 0xFFFFFFFF
    return struct.unpack("<f", struct.pack("<I", bits))[0]


def format_number(value):
    if value == int(value):
        return str(int(value))
    text = f"{value:.4f}".rstrip("0").rstrip(".")
    return text


def parse_ctor_constants(methods):
    """Offsets written as float literals in the constructor."""
    constants = {}
    for signature, instructions in methods.items():
        if ".ctor(" not in signature or ".cctor" in signature:
            continue
        for instruction in instructions:
            match = re.match(r"Move \[[a-z0-9]+\+(\d+)\], (0x[0-9A-Fa-f]+)$", instruction)
            if not match:
                continue
            offset = int(match.group(1))
            raw = match.group(2)
            as_int = int(raw, 16)
            # Small integers (1, 10, 25) are counts, not float bit patterns.
            if as_int < 0x1000:
                constants[offset] = str(as_int)
            else:
                constants[offset] = format_number(float_from_hex(raw))
    return constants


def parse_value(ops, constants):
    """Turn the xmm0 operations before a store into a short formula."""
    terms = []
    for op, source in ops:
        if source.startswith("["):
            match = re.search(r"\+(\d+)\]", source)
            if match and int(match.group(1)) in constants:
                value = constants[int(match.group(1))]
            else:
                value = source
        else:
            value = source
        symbol = "*" if op == "Multiply" else "+"
        terms.append((symbol, value))
    if not terms:
        return "amount"
    text = "amount"
    for symbol, value in terms:
        text += f" {symbol} {value}"
    return text


def method_name(signature):
    """`System.Void Foo(Bar)` -> `Foo`."""
    head = signature.split("(")[0].strip()
    return head.split()[-1] if head.split() else head


def parse_stats(methods, constants):
    """All SetStat calls, with the method that makes them."""
    calls = []
    for signature, instructions in methods.items():
        method = method_name(signature)
        if method == "SetStat":
            continue
        stat = None
        modify = 0
        ops = []
        for instruction in instructions:
            match = re.match(r"Move \[[a-z0-9]+\+16\], (\d+)$", instruction)
            if match:
                stat = int(match.group(1))
                ops = []
                continue
            match = re.match(r"Move \[[a-z0-9]+\+20\], (\d+)$", instruction)
            if match:
                modify = int(match.group(1))
                continue
            match = re.match(r"(Multiply|Add) xmm0, xmm0, (.+)$", instruction)
            if match:
                ops.append((match.group(1), match.group(2)))
                continue
            if instruction.startswith("Call ItemBase.SetStat"):
                if stat is not None:
                    calls.append(
                        {
                            "method": method,
                            "stat": stat,
                            "modify": modify,
                            "value": parse_value(ops, constants),
                        }
                    )
                stat = None
                modify = 0
                ops = []
    return calls


def parse_hooks(methods):
    hooks = []
    for signature, instructions in methods.items():
        method = method_name(signature)
        if method in NOOP_METHODS or method in PRESENTATION_METHODS:
            continue
        body = [line for line in instructions if line != "Return"]
        if not body:
            continue
        if method.startswith("get_") or method.startswith("set_"):
            continue
        hooks.append(method)
    return hooks


def factory_classes():
    """Ordered Item* classes from the factory's jump table."""
    text = read_text(FACTORY)
    isil_start = text.index("ISIL:")
    lines = [re.match(r"\t\d+ (.+)$", line).group(1).strip()
             for line in text[isil_start:].splitlines()
             if re.match(r"\t\d+ (.+)$", line)]
    classes = []
    for index, line in enumerate(lines[:-1]):
        match = re.search(
            r"Move rcx, typeof\(.*Items\.(?:ItemImplementations\.)?(Item\w+)\)", line
        )
        if match and "il2cpp_codegen_object_new" in lines[index + 1]:
            classes.append(match.group(1))
    return classes


def map_slots(eitems, classes):
    """Zip the branch heads onto EItem order, inserting the shared slots."""
    slots = {}
    cursor = 0
    for slot, number in sorted(eitems.items(), key=lambda pair: pair[1]):
        if slot in SHARED_SLOTS:
            slots[slot] = SHARED_SLOTS[slot]
            continue
        slots[slot] = classes[cursor]
        cursor += 1
    if cursor != len(classes):
        raise SystemExit(
            f"factory has {len(classes)} branches for {len(eitems) - len(SHARED_SLOTS)} slots"
        )
    return slots


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="verify the doc is current")
    args = parser.parse_args()

    rows = json.loads(ITEMS_JSON.read_text(encoding="utf-8"))
    enums = json.loads(ENUMS_JSON.read_text(encoding="utf-8"))
    eitem = enums["EItem"]
    estats = {value: name for name, value in enums["EStat"].items()}
    emodify = {value: name for name, value in enums["EStatModifyType"].items()}

    classes = factory_classes()
    slots = map_slots(eitem, classes)

    by_slot = {row["eItemName"]: row for row in rows}

    lines = []
    lines.append("# Item donor table (`EItem`)")
    lines.append("")
    lines.append(
        "Generated by `mod/tools/data/dump-item-donors.py` from "
        "`mod/design/megabonk/items.json` and the IL2CPP ISIL dump. "
        "Do not edit by hand; rerun the script."
    )
    lines.append("")
    lines.append(
        "Item effects are **code-side**: `ItemFactory` dispatches each `EItem` to one "
        "`ItemImplementations.Item*` class and `ItemData` carries only the card. "
        "Renaming a slot therefore keeps the donor class, so a PoE relic may only "
        "take a slot whose native effect its text can state honestly."
    )
    lines.append("")
    lines.append(
        "`stats` are the `StatModifier` writes made through `ItemBase.SetStat` "
        "(`EStat` / `EStatModifyType` / value; `amount` is the item's stack count). "
        "A bracketed operand (`[rax]`, `[rdi+48]`) is a field the dump does not "
        "resolve, so the number has to be read from that class. `hooks` are the "
        "non-trivial code paths: procs, ticks and kill/heal/damage handlers. "
        "A row with neither is read outside its class: Za Warudo's lethal-damage "
        "check lives in `PlayerHealth`, Anvil's extra upgrade stat in "
        "`WeaponUpgrade`, and Overpowered Lamp's proc count in `ItemUtility`; "
        "the two `ItemNoImplementation` rows are the joke Golden Ring and the "
        "quest keys."
    )
    lines.append("")
    lines.append("| EItem | Slot | Class | Rarity | Price | Pool | Max | Native text | stats | hooks |")
    lines.append("|---:|---|---|---|---:|---|---:|---|---|---|")

    missing = []
    for slot, number in sorted(eitem.items(), key=lambda pair: pair[1]):
        row = by_slot.get(slot)
        if row is None:
            missing.append(slot)
            continue
        fields = row
        name = fields["name"]
        description = fields["description"]
        rarity = fields["rarityName"]
        price = format_number(float(fields["price"]))
        pool = "yes" if fields["inItemPool"] else "no"
        max_amount = int(fields["maxAmount"])
        max_per_run = int(fields["maxAmountPerRun"])
        max_text = str(max_amount)
        if max_per_run:
            max_text += f" / {max_per_run} per run"
        cls = slots[slot]
        path = IMPL_DIR / f"{cls}.txt"
        if not path.exists():
            # ItemSuckyHoof lives in the parent Items namespace.
            path = IMPL_DIR.parent / f"{cls}.txt"
        if not path.exists():
            lines.append(
                f"| {number} | {name} | `{cls}` | {rarity} | {price} | {pool} | "
                f"{max_text} | {description} | _no dump_ | _no dump_ |"
            )
            continue
        methods = split_methods(read_text(path))
        constants = parse_ctor_constants(methods)
        stats = parse_stats(methods, constants)
        hooks = parse_hooks(methods)

        stat_text = "; ".join(
            f"{estats.get(call['stat'], call['stat'])} "
            f"{emodify.get(call['modify'], call['modify'])} {call['value']}"
            + (f" ({call['method']})" if call["method"] != "OnInitOrAmountChanged" else "")
            for call in stats
        ) or "—"
        hook_text = ", ".join(hooks) or "—"
        lines.append(
            f"| {number} | {name} | `{cls}` | {rarity} | {price} | {pool} | "
            f"{max_text} | {description} | {stat_text} | {hook_text} |"
        )

    lines.append("")
    lines.append(
        f"{len(rows)} `ItemData` rows, {len(eitem)} `EItem` members, "
        f"{len(classes)} factory branches, {len(slots)} mapped slots."
    )
    if missing:
        lines.append("")
        lines.append("No `ItemData` row for: " + ", ".join(missing))

    output = "\n".join(lines) + "\n"
    if args.check:
        if not OUT.exists() or OUT.read_text(encoding="utf-8") != output:
            print("ITEM_DONORS.md is stale; rerun without --check")
            return 1
        print(f"ITEM_DONORS.md is current ({len(rows)} rows, {len(slots)} slots)")
        return 0

    OUT.write_text(output, encoding="utf-8", newline="\n")
    print(f"wrote {OUT.relative_to(ROOT)} ({len(rows)} rows, {len(slots)} slots)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
