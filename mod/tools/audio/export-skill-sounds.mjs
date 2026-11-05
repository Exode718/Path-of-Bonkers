// Export one cast sound per class skill from the Path of Exile client.
//
// The client ships its skill sounds as FMOD FSB5 banks under
// <PoE>/FMOD/Desktop/*.bank, not as .ogg files. An FSB5 Vorbis subsound keeps
// raw Vorbis packets; the codebooks live outside the bank in FMOD's fixed setup
// table (lab/tools/vorbis_codebooks_fsb.h, one packet per CRC32). Each sample
// header names the setup it needs in its first extra block, so a subsound can
// be rebuilt as a playable Ogg file: identification + comment + that setup
// packet, then the sample's own packets, each prefixed by a uint16 length.
//
// Node only (no npm package): the bank parser, the setup table and the Ogg page
// writer are here; ffmpeg (the one prepare-data already expects) trims and
// normalises each clip to 44.1 kHz mono 16-bit PCM.
//
// The clips are written as WAV (44.1 kHz mono 16-bit PCM). `SkillAudio` parses
// the WAV itself and fills an `AudioClip` through its `PCMReaderCallback`:
// `AudioClip.SetData(float[])` throws across this interop (the missing
// `ReadOnlySpan.GetPinnableReference`, the same trap as `LoadImage`), and the
// `UnityWebRequest` audio helpers are stripped from the build (their
// constructors are absent), so the callback is the one path that only uses
// `AudioClip.Create`, which exists.
//
// Usage:
//   node mod/tools/audio/export-skill-sounds.mjs [--banks <dir>] [--out <dir>]
//   node mod/tools/audio/export-skill-sounds.mjs --check
//
// Defaults: banks = C:\Program Files (x86)\Steam\steamapps\common\Path of Exile\FMOD\Desktop
//           out   = mod/art/game/sounds

import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { crc32 } from "node:zlib";

const here = dirname(fileURLToPath(import.meta.url));
const modRoot = resolve(here, "..", "..");
const repoRoot = resolve(modRoot, "..");
const headerPath = join(repoRoot, "lab", "tools", "vorbis_codebooks_fsb.h");

const DEFAULT_BANKS = "C:/Program Files (x86)/Steam/steamapps/common/Path of Exile/FMOD/Desktop";
const DEFAULT_OUT = join(modRoot, "art", "game", "sounds");
const FFMPEG = "C:/Program Files/ffmpeg/bin/ffmpeg.exe";

/// One cast sound per skill id in design/arena/skills.json. The sample name is
/// the client's own sound inside that bank; the loop samples are the bow's
/// release for Ice Shot (it has no one-shot cast in its bank).
const SKILLS = [
  { id: "fireball", bank: "FireballNew.bank", sample: "Start_6", seconds: 1.0 },
  { id: "ice-shot", bank: "SkillsIceShot.bank", sample: "IceShot_01.loop", seconds: 0.7 },
  { id: "ice-nova", bank: "SkillsIceNova.bank", sample: "IceNova_01", seconds: 1.4 },
  { id: "arc", bank: "SkillsArc.bank", sample: "Arc_01", seconds: 1.4 },
  { id: "cyclone", bank: "_Effects_Spells_whirlwind.bank", sample: "Hands_03", seconds: 0.9 },
  { id: "righteous-fire", bank: "_Effects_Spells_ri_fire.bank", sample: "Start_01", seconds: 1.0 },
  { id: "viper-strike", bank: "SkillsViperStrike.bank", sample: "Viper_Strike_01", seconds: 0.8 },
];

// ------------------------------------------------------------------ setup table

/// Reads vorbis_codebooks_fsb.h: the 161 FMOD setup packets, keyed by the CRC32
/// their array name carries (verified: name === crc32(packet) on all of them).
function loadSetups() {
  if (!existsSync(headerPath)) {
    throw new Error(`setup table missing: ${headerPath} (lab/tools/vorbis_codebooks_fsb.h)`);
  }
  const text = readFileSync(headerPath, "utf8");
  const re = /static const uint8_t vcb_([0-9a-f]{8})\[\] = \{([\s\S]*?)\};/g;
  const setups = new Map();
  let m;
  while ((m = re.exec(text))) {
    const hex = m[2].match(/0x[0-9a-fA-F]{2}/g) || [];
    const packet = Buffer.from(hex.map((x) => parseInt(x, 16)));
    const crc = crc32(packet).toString(16).padStart(8, "0");
    if (crc !== m[1]) {
      throw new Error(`setup table is stale: vcb_${m[1]} CRCs to ${crc}`);
    }
    setups.set(m[1], packet);
  }
  if (setups.size === 0) {
    throw new Error("setup table has no packets");
  }
  return setups;
}

// ------------------------------------------------------------------ FSB5 parse

const RATES = [4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000];
const CHANNELS = [1, 2, 6, 8];

/// Parses an FSB5 chunk inside an FMOD bank (same header layout as
/// lab/tools/fsb5.mjs, plus each sample's packet list and setup CRC).
function parseFsb5(buf) {
  const start = buf.indexOf("FSB5");
  if (start < 0) {
    throw new Error("no FSB5 chunk");
  }

  const version = buf.readUInt32LE(start + 0x04);
  const total = buf.readUInt32LE(start + 0x08);
  const sampleHeaderSize = buf.readUInt32LE(start + 0x0c);
  const nameTableSize = buf.readUInt32LE(start + 0x10);
  const dataSize = buf.readUInt32LE(start + 0x14);
  const codec = buf.readUInt32LE(start + 0x18);
  const baseHeaderSize = version === 1 ? 0x3c : 0x40;
  const dataStart = start + baseHeaderSize + sampleHeaderSize + nameTableSize;
  const namesStart = start + baseHeaderSize + sampleHeaderSize;

  const samples = [];
  let offset = start + baseHeaderSize;
  for (let i = 0; i < total; i += 1) {
    const sampleMode = buf.readBigUInt64LE(offset);
    const numSamples = Number((sampleMode >> 34n) & 0x3fffffffn);
    const dataOffset = Number((sampleMode >> 7n) & 0x07ffffffn) * 32;
    let channels = CHANNELS[Number((sampleMode >> 5n) & 0x03n)];
    const rateIndex = Number((sampleMode >> 1n) & 0x0fn);
    let rate = RATES[rateIndex];
    let setupCrc = null;
    let headerSize = 8;

    if (Number(sampleMode & 1n)) {
      let extraOffset = offset + 8;
      const max = start + baseHeaderSize + sampleHeaderSize;
      while (extraOffset < max) {
        const flag = buf.readUInt32LE(extraOffset);
        const type = (flag >> 25) & 0x7f;
        const size = (flag >> 1) & 0xffffff;
        const more = flag & 1;
        if (type === 0x0b && size >= 4 && setupCrc === null) {
          setupCrc = buf.readUInt32LE(extraOffset + 4).toString(16).padStart(8, "0");
        }
        if (type === 0x02 && size >= 4) rate = buf.readInt32LE(extraOffset + 4);
        if (type === 0x01 && size >= 1) channels = buf.readUInt8(extraOffset + 4);
        extraOffset += 4 + size;
        headerSize += 4 + size;
        if (!more) break;
      }
    }

    let name = null;
    if (nameTableSize > 0) {
      const nameOffset = buf.readUInt32LE(namesStart + 4 * i);
      const nameAt = namesStart + nameOffset;
      const end = buf.indexOf(0, nameAt);
      name = buf.toString("latin1", nameAt, end);
    }

    samples.push({ name, channels, rate, numSamples, dataOffset, setupCrc });
    offset += headerSize;
  }

  for (let i = 0; i < samples.length; i += 1) {
    const next = i + 1 < samples.length ? samples[i + 1].dataOffset : dataSize;
    const bytes = next - samples[i].dataOffset;
    const at = dataStart + samples[i].dataOffset;
    const packets = [];
    let cursor = 0;
    while (cursor + 2 <= bytes) {
      const length = buf.readUInt16LE(at + cursor);
      if (length === 0) {
        // dataOffset is expressed in 32-byte units, so a subsound can end in
        // alignment padding after its last packet.
        break;
      }
      cursor += 2;
      if (cursor + length > bytes) {
        throw new Error(`sample '${samples[i].name}': bad packet length ${length} at ${cursor}`);
      }
      packets.push(buf.subarray(at + cursor, at + cursor + length));
      cursor += length;
    }
    samples[i].packets = packets;
    samples[i].bytes = bytes;
  }

  return { start, version, codec, samples };
}

// ------------------------------------------------------------------ Ogg writer

/// Ogg's CRC: polynomial 0x04c11db7, init 0, no reflection, no final xor.
function oggCrc(bytes) {
  let crc = 0;
  for (const byte of bytes) {
    crc ^= byte << 24;
    for (let bit = 0; bit < 8; bit += 1) {
      crc = crc & 0x80000000 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
    }
  }
  return crc >>> 0;
}

let oggSerial = 0x57524145;

function oggPage({ headerType, granule, packets, sequence }) {
  const lacing = [];
  const body = [];
  for (const packet of packets) {
    let remaining = packet.length;
    while (remaining >= 255) {
      lacing.push(255);
      remaining -= 255;
    }
    lacing.push(remaining);
    body.push(packet);
  }
  if (lacing.length > 255) {
    throw new Error("too many segments for one Ogg page");
  }

  const payload = Buffer.concat(body);
  const header = Buffer.alloc(27 + lacing.length);
  header.write("OggS", 0, "latin1");
  header.writeUInt8(0, 4);
  header.writeUInt8(headerType, 5);
  header.writeBigUInt64LE(BigInt(granule), 6);
  header.writeUInt32LE(oggSerial, 14);
  header.writeUInt32LE(sequence, 18);
  header.writeUInt32LE(0, 22);
  header.writeUInt8(lacing.length, 26);
  lacing.forEach((value, index) => header.writeUInt8(value, 27 + index));

  const page = Buffer.concat([header, payload]);
  page.writeUInt32LE(oggCrc(page), 22);
  return page;
}

/// Rebuilds one subsound as an Ogg Vorbis file (three header pages, then the
/// audio packets on one page with the sample count as the final granule).
function toOgg(sample, setup) {
  const identification = Buffer.alloc(30);
  let cursor = 0;
  identification.writeUInt8(0x01, cursor); cursor += 1;
  identification.write("vorbis", cursor, "latin1"); cursor += 6;
  identification.writeUInt32LE(0, cursor); cursor += 4;
  identification.writeUInt8(sample.channels, cursor); cursor += 1;
  identification.writeUInt32LE(sample.rate, cursor); cursor += 4;
  identification.writeUInt32LE(0, cursor); cursor += 4;
  identification.writeUInt32LE(0, cursor); cursor += 4;
  identification.writeUInt32LE(0, cursor); cursor += 4;
  // FMOD's FSB5 Vorbis uses 256/2048 sample blocks (exponents 8 and 11).
  identification.writeUInt8((11 << 4) | 8, cursor); cursor += 1;
  identification.writeUInt8(1, cursor);

  const vendor = Buffer.from("Wraeclast", "latin1");
  const comment = Buffer.alloc(7 + 4 + vendor.length + 4 + 1);
  let at = 0;
  comment.writeUInt8(0x03, at); at += 1;
  comment.write("vorbis", at, "latin1"); at += 6;
  comment.writeUInt32LE(vendor.length, at); at += 4;
  vendor.copy(comment, at); at += vendor.length;
  comment.writeUInt32LE(0, at); at += 4;
  comment.writeUInt8(1, at);

  const pages = [
    oggPage({ headerType: 0x02, granule: 0, packets: [identification], sequence: 0 }),
    oggPage({ headerType: 0x00, granule: 0, packets: [comment, setup], sequence: 1 }),
  ];

  // The audio packets of one short SFX fit on one page (< 65 KB); a longer
  // sample is split on packet boundaries with granule 0 until the last page.
  const groups = [];
  let group = [];
  let size = 0;
  for (const packet of sample.packets) {
    if (group.length > 0 && size + packet.length > 60000) {
      groups.push(group);
      group = [];
      size = 0;
    }
    group.push(packet);
    size += packet.length;
  }
  if (group.length > 0) {
    groups.push(group);
  }
  groups.forEach((packets, index) => {
    const last = index === groups.length - 1;
    pages.push(
      oggPage({
        headerType: last ? 0x04 : 0x00,
        granule: last ? sample.numSamples : 0,
        packets,
        sequence: 2 + index,
      }),
    );
  });

  oggSerial += 1;
  return Buffer.concat(pages);
}

// ------------------------------------------------------------------ ffmpeg

function run(command, args) {
  const result = spawnSync(command, args, { encoding: "utf8" });
  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(`${command} failed (${result.status}):\n${result.stderr || result.stdout}`);
  }
  return result;
}

/// Trims leading/trailing silence, peak-normalises to −1 dBFS and writes a
/// 44.1 kHz mono 16-bit PCM WAV (parsed by SkillAudio into the PCM callback).
function convert(oggPath, outPath, seconds) {
  const trim = "silenceremove=start_periods=1:start_threshold=-55dB:start_silence=0.02," +
    "areverse,silenceremove=start_periods=1:start_threshold=-55dB:start_silence=0.02,areverse";
  const measured = run(FFMPEG, [
    "-hide_banner", "-i", oggPath,
    "-af", `${trim},volumedetect`,
    "-f", "null", "-",
  ]);
  const peak = /max_volume:\s*(-?[\d.]+) dB/.exec(measured.stderr);
  if (!peak) {
    throw new Error(`could not measure ${oggPath}`);
  }
  const gain = Math.max(-12, Math.min(24, -1 - Number(peak[1])));
  const fade = seconds
    ? `,afade=t=out:st=${Math.max(0, seconds - 0.08).toFixed(2)}:d=0.08`
    : "";

  run(FFMPEG, [
    "-hide_banner", "-y", "-i", oggPath,
    ...(seconds ? ["-t", String(seconds)] : []),
    "-ac", "1", "-ar", "44100", "-sample_fmt", "s16",
    "-af", `${trim},volume=${gain.toFixed(2)}dB${fade}`,
    "-c:a", "pcm_s16le", outPath,
  ]);
}

// ------------------------------------------------------------------ check

/// Offline check for the battery: every skill has a RIFF/WAVE file that is mono
/// 44.1 kHz 16-bit PCM with a data chunk of real length (a truncated file fails).
function check(outDir) {
  let failures = 0;
  for (const skill of SKILLS) {
    const path = join(outDir, `${skill.id}.wav`);
    if (!existsSync(path)) {
      console.error(`FAIL ${skill.id}: missing ${path}`);
      failures += 1;
      continue;
    }
    const wav = readFileSync(path);
    if (wav.toString("latin1", 0, 4) !== "RIFF" || wav.toString("latin1", 8, 12) !== "WAVE") {
      console.error(`FAIL ${skill.id}: not a RIFF/WAVE file`);
      failures += 1;
      continue;
    }
    let fmt = null;
    let data = null;
    for (let at = 12; at + 8 <= wav.length;) {
      const id = wav.toString("latin1", at, at + 4);
      const size = wav.readUInt32LE(at + 4);
      if (id === "fmt ") fmt = at + 8;
      if (id === "data") data = { at: at + 8, size };
      at += 8 + size + (size & 1);
    }
    const channels = fmt === null ? 0 : wav.readUInt16LE(fmt + 2);
    const rate = fmt === null ? 0 : wav.readUInt32LE(fmt + 4);
    const bits = fmt === null ? 0 : wav.readUInt16LE(fmt + 14);
    if (fmt === null || channels !== 1 || rate !== 44100 || bits !== 16) {
      console.error(`FAIL ${skill.id}: fmt ${channels}ch ${rate}Hz ${bits}-bit (want 1ch 44100Hz 16-bit)`);
      failures += 1;
      continue;
    }
    if (data === null || data.size < 2000 || data.at + data.size > wav.length) {
      console.error(`FAIL ${skill.id}: empty or truncated data chunk`);
      failures += 1;
      continue;
    }
    console.log(`ok ${skill.id}: ${(data.size / 2 / 44100).toFixed(2)} s`);
  }
  if (failures > 0) {
    console.error(`${failures} skill sound(s) failed`);
    process.exit(1);
  }
  console.log(`skill sounds: ${SKILLS.length}/${SKILLS.length} ok`);
}

// ------------------------------------------------------------------ main

function option(args, name, fallback) {
  const at = args.indexOf(name);
  return at >= 0 && at + 1 < args.length ? args[at + 1] : fallback;
}

const args = process.argv.slice(2);
const outDir = option(args, "--out", DEFAULT_OUT);

if (args.includes("--check")) {
  check(outDir);
  process.exit(0);
}

const banksDir = option(args, "--banks", DEFAULT_BANKS);
if (!existsSync(banksDir)) {
  console.error(`client banks not found: ${banksDir}`);
  process.exit(1);
}
if (!existsSync(FFMPEG)) {
  console.error(`ffmpeg not found: ${FFMPEG}`);
  process.exit(1);
}

const setups = loadSetups();
mkdirSync(outDir, { recursive: true });
const temp = mkdtempSync(join(tmpdir(), "wraeclast-sounds-"));

try {
  for (const skill of SKILLS) {
    const bankPath = join(banksDir, skill.bank);
    if (!existsSync(bankPath)) {
      throw new Error(`${skill.id}: bank missing ${bankPath}`);
    }
    const bank = parseFsb5(readFileSync(bankPath));
    if (bank.codec !== 0x0f) {
      throw new Error(`${skill.id}: ${skill.bank} is not Vorbis (codec ${bank.codec})`);
    }
    const sample = bank.samples.find((entry) => entry.name === skill.sample);
    if (!sample) {
      throw new Error(`${skill.id}: '${skill.sample}' not in ${skill.bank}`);
    }
    const setup = setups.get(sample.setupCrc);
    if (!setup) {
      throw new Error(`${skill.id}: setup ${sample.setupCrc} is not in the table`);
    }

    const rawPath = join(temp, `${skill.id}-raw.ogg`);
    const outPath = join(outDir, `${skill.id}.wav`);
    writeFileSync(rawPath, toOgg(sample, setup));
    convert(rawPath, outPath, skill.seconds);
    const seconds = sample.numSamples / sample.rate;
    console.log(
      `${skill.id}: ${skill.bank} '${skill.sample}' ${sample.channels}ch ${sample.rate}Hz ` +
        `${seconds.toFixed(2)} s -> ${outPath}`,
    );
  }
} finally {
  rmSync(temp, { recursive: true, force: true });
}

console.log(`exported ${SKILLS.length} skill sounds to ${outDir}`);
