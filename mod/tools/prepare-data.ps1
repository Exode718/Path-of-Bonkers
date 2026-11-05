# Collects the data the plugin loads at runtime: class sheet, portraits, models,
# skills and skill VFX.
#
# Inputs, all relative to mod/ (gitignored client art; the JSON sheets are our
# own design data):
#   design/arena/classes.json          our class sheet
#   design/arena/skills.json           our skill sheet
#   data/tomes.json                    our tome sheet -> Megabonk ETome slots
#   data/bosses.json                   our Atlas boss roster (floors, minibosses)
#   art/class/{Class}.png              PoE class portrait source
#   art/game/classes/{Class}.png       PoE class portrait (card icon)
#   art/game/models/{Class}.glb        PoE class model
#   art/game/models/{creature}.glb     PoE enemy creature (bestiary)
#   art/game/weapons/{weapon}.glb      starter weapon, grip at the origin
#   art/game/effects/{skill}.glb       PoE projectile/beam meshes
#   art/game/ui/vfx/{sheet}.png        client effect sheets (Fireball, Arc,
#                                      Ice Nova, Cyclone, Viper Strike,
#                                      Righteous Fire; S9)
#   art/game/sounds/{skill}.wav        client cast sounds, 44.1 kHz mono PCM
#                                      (SND; export-skill-sounds.mjs)
#   art/game/ui/poe-logo.png           client Path of Exile logo (V6)
#   art/game/terrain/{name}.png        client PoE terrain art (A1: Forest floor,
#                                      cliff, soil, grass tuft)
#
# Output: Wraeclast/assets/ (gitignored), copied next to the plugin DLL by
# `dotnet build`.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'design\arena\classes.json'
$skills = Join-Path $root 'design\arena\skills.json'
$cropped = Join-Path $root 'art\game\classes'
$models = Join-Path $root 'art\game\models'
$vfx = Join-Path $root 'art\game\ui\vfx'
$target = Join-Path $root 'Wraeclast\assets'

if (-not (Test-Path $source)) { throw "Missing class table: $source" }

$icons = Join-Path $target 'icons'
$modelDir = Join-Path $target 'models'
$weaponDir = Join-Path $target 'weapons'
$effectDir = Join-Path $target 'effects'
$vfxDir = Join-Path $target 'vfx'
$soundDir = Join-Path $target 'sounds'
$terrainDir = Join-Path $target 'terrain'
$propDir = Join-Path $modelDir 'props'
$fullArt = Join-Path $root 'art\game\classes\full'
New-Item -ItemType Directory -Force -Path $icons, $modelDir, $weaponDir, $effectDir, $vfxDir, $soundDir, $terrainDir, $propDir | Out-Null
Copy-Item $source (Join-Path $target 'classes.json') -Force

if (Test-Path $skills) {
    Copy-Item $skills (Join-Path $target 'skills.json') -Force
}
else {
    Write-Warning "Skill table missing: $skills"
}

# PoE tomes on the game's own ETome slots (data/tomes.json carries the
# slot, the card text and the native statModifier it expects to find).
$tomes = Join-Path $root 'data\tomes.json'
if (Test-Path $tomes) {
    Copy-Item $tomes (Join-Path $target 'tomes.json') -Force
}
else {
    Write-Warning "Tome table missing: $tomes"
}

# The mod's reward table (data/rewards.json): the four code-side tomes now,
# every tome/relic/mod entry once R5 exists.
$rewards = Join-Path $root 'data\rewards.json'
if (Test-Path $rewards) {
    Copy-Item $rewards (Join-Path $target 'rewards.json') -Force
}
else {
    Write-Warning "Reward table missing: $rewards"
}

# PoE relics on the game's own EItem slots (data/items.json carries the
# slot, the card text and, since S5, every relic's own stats/triggers).
$items = Join-Path $root 'data\items.json'
if (Test-Path $items) {
    Copy-Item $items (Join-Path $target 'items.json') -Force
}
else {
    Write-Warning "Item table missing: $items"
}

# The Atlas boss roster (data/bosses.json): floor and miniboss roles, the
# display name and the mesh each role wears.
$bosses = Join-Path $root 'data\bosses.json'
if (Test-Path $bosses) {
    Copy-Item $bosses (Join-Path $target 'bosses.json') -Force
}
else {
    Write-Warning "Boss roster missing: $bosses"
}

# Skill VFX sheets: the Fireball projectile and Arc bolt, plus the S9 client
# particle sheets the four remaining skills draw (Ice Nova shockwave ring,
# Cyclone swirl, Viper Strike spiral, Righteous Fire rune circle). A missing
# sheet is not fatal: the runtime keeps its procedural sprite.
$vfxSheets = 'fireball', 'arc', 'ice-nova', 'cyclone', 'viper-strike', 'righteous-fire', 'ash-cloud'
foreach ($sheet in $vfxSheets) {
    $sheetPath = Join-Path $vfx "$sheet.png"
    if (Test-Path $sheetPath) {
        Copy-Item $sheetPath (Join-Path $vfxDir "$sheet.png") -Force
    }
    else {
        Write-Warning "VFX sheet missing: $sheetPath"
    }
}

# SND: the class skills' cast sounds, one WAV per skills.json id. Exported from
# the client's FMOD banks by mod/tools/audio/export-skill-sounds.mjs. A missing
# file is not fatal: that skill stays silent (SkillAudio logs it once).
$sounds = Join-Path $root 'art\game\sounds'
$soundFiles = 0
if (Test-Path $sounds) {
    Get-ChildItem $sounds -Filter *.wav | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $soundDir $_.Name) -Force
        $soundFiles++
    }
}
else {
    Write-Warning "Skill sounds missing: $sounds (run node mod/tools/audio/export-skill-sounds.mjs)"
}

# The 0.9.193 attempt shipped OGG through UnityWebRequest, which this build
# cannot construct; the runtime reads WAV through the clip's PCM callback. Drop
# any stale staged OGG so the plugin folder matches the current export.
Get-ChildItem $soundDir -Filter *.ogg -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Main menu art (V6): the client's own Path of Exile logo replaces the game's
# title. Nothing else on the menu changes (the button re-skin was cancelled).
# A missing file is not fatal: the menu keeps Megabonk's own logo.
$menuArt = Join-Path $target 'ui'
New-Item -ItemType Directory -Force -Path $menuArt | Out-Null
$menuLogoPath = Join-Path $root 'art\game\ui\poe-logo.png'
if (Test-Path $menuLogoPath) {
    Copy-Item $menuLogoPath (Join-Path $menuArt 'poe-logo.png') -Force
}
else {
    Write-Warning "Menu logo missing: $menuLogoPath"
}

# A1: the Forest ground and terrain edges. The five floor tiles, the cliff, the
# under-overhang soil and one grass tuft are the client's own terrain art,
# re-bound to Megabonk's materials at runtime (TerrainSwap). A missing file is
# not fatal: that slot keeps the host texture.
$terrainArt = Join-Path $root 'art\game\terrain'
if (Test-Path $terrainArt) {
    Get-ChildItem $terrainArt -Filter *.png | Copy-Item -Destination $terrainDir -Force
}
else {
    Write-Warning "Terrain art missing: $terrainArt"
}

# A2/A3a/A3b: the client props, chests and shrine totem the swap passes draw
# over Megabonk's own. Only what Data/PropSwap.cs, Data/ChestSwap.cs and
# Data/ShrineSwap.cs actually map is shipped; the list is the same ids those
# three files name.
$propArt = Join-Path $root 'art\game\models\props'
$propIds = @(
    'forest-tree-1', 'forest-tree-2', 'forest-tree-3', 'forest-tree-dead',
    'kaom-rock', 'ruin-wall', 'forest-statue',
    'chest-normal', 'chest-normal-open', 'poe-shrine-totem', 'greed-stickskull',
    'poe-statue-moai', 'poe-pot-1', 'poe-pot-2', 'poe-pot-3',
    'poe-microwave', 'poe-cursed-shrine', 'poe-challenge-shrine', 'poe-magnet-shrine'
)
$propsShipped = 0
foreach ($propId in $propIds) {
    $propPath = Join-Path $propArt "$propId.glb"
    if (Test-Path $propPath) {
        Copy-Item $propPath (Join-Path $propDir "$propId.glb") -Force
        $propsShipped++
    }
    else {
        Write-Warning "Prop missing: $propPath (node mod/tools/poe-gltf/build-glb.mjs --props)"
    }
}

# S5+: PoE unique icons for the relics (mod/art/game/ui/items/{id}.png). The
# 79 slots map to 77 client uniques; the two the client has no art for keep the
# donor icon. A missing PNG is not fatal (the card falls through to the donor).
#
# The card slot is square (the donor art is 80x80) while PoE unique art is not
# (156x236, 156x312, ...), so a plain copy is stretched by the slot and reads
# squashed. Fit every icon inside an 80x80 transparent square instead: the art
# keeps its aspect (a 400x800 source becomes 40x80 centred) and no pixel is
# distorted.
$relicArt = Join-Path $root 'art\game\ui\items'
$itemIconDir = Join-Path $icons 'items'
$itemIconSize = 80
New-Item -ItemType Directory -Force -Path $itemIconDir | Out-Null
if (Test-Path $relicArt) {
    Add-Type -AssemblyName System.Drawing
    foreach ($file in Get-ChildItem $relicArt -Filter *.png) {
        $image = [System.Drawing.Image]::FromFile($file.FullName)
        try {
            # Both operands must be doubles: Windows PowerShell resolves
            # [Math]::Min(80, 0.26) to the Int32 overload and truncates the
            # fit to zero, which produced a 1x1 icon for every source whose
            # width or height divided 80 exactly (blood-thorn, void-battery).
            $scale = [Math]::Min(
                [double]$itemIconSize / [double]$image.Width,
                [double]$itemIconSize / [double]$image.Height)
            $width = [Math]::Max(1, [int][Math]::Round($image.Width * $scale))
            $height = [Math]::Max(1, [int][Math]::Round($image.Height * $scale))
            $canvas = New-Object System.Drawing.Bitmap $itemIconSize, $itemIconSize
            $graphics = [System.Drawing.Graphics]::FromImage($canvas)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage(
                $image,
                (New-Object System.Drawing.Rectangle ([int](($itemIconSize - $width) / 2)), ([int](($itemIconSize - $height) / 2)), $width, $height))
            $graphics.Dispose()
            $canvas.Save((Join-Path $itemIconDir $file.Name), [System.Drawing.Imaging.ImageFormat]::Png)
            $canvas.Dispose()
        }
        finally {
            $image.Dispose()
        }
    }
}
else {
    Write-Warning "Relic icon folder missing: $relicArt (run mod/tools/data/export-item-icons.py)"
}

# PoE skill icons for the weapon row (V3): with the weapon lock there is no
# native weapon in the HUD slot, so it shows the class's one skill instead. The
# files are named by the skills.json id (arc, fireball, ice-nova, ...).
$skillArt = Join-Path $root 'art\game\ui\skills'
$skillIconDir = Join-Path $icons 'skills'
New-Item -ItemType Directory -Force -Path $skillIconDir | Out-Null
if ((Test-Path $skills) -and (Test-Path $skillArt)) {
    $skillSheet = Get-Content $skills -Raw | ConvertFrom-Json
    foreach ($skill in $skillSheet) {
        $iconPath = Join-Path $skillArt "$($skill.id).png"
        if (Test-Path $iconPath) {
            Copy-Item $iconPath (Join-Path $skillIconDir "$($skill.id).png") -Force
        }
        else {
            Write-Warning "Skill icon missing: $iconPath"
        }
    }
}
else {
    Write-Warning "Skill icons missing: $skillArt"
}

$classes = Get-Content $source -Raw | ConvertFrom-Json
foreach ($class in $classes) {
    $id = $class.id

    # Megabonk cards are square. The painted class splash is the real portrait
    # art; crop a square from its upper centre (head and torso) instead of using
    # the 180 px in-game render that some classes ship as "portrait".
    $splash = Join-Path $fullArt "$id.png"
    if (-not (Test-Path $splash)) {
        $splash = Join-Path $cropped "$id.png"
    }

    if (Test-Path $splash) {
        Add-Type -AssemblyName System.Drawing
        $image = [System.Drawing.Image]::FromFile($splash)
        try {
            $side = [Math]::Min($image.Width, [int]($image.Height * 0.74))
            $sx = [int](($image.Width - $side) / 2)
            $sy = [int]($image.Height * 0.02)
            $size = 512
            $canvas = New-Object System.Drawing.Bitmap $size, $size
            $graphics = [System.Drawing.Graphics]::FromImage($canvas)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage(
                $image,
                (New-Object System.Drawing.Rectangle 0, 0, $size, $size),
                (New-Object System.Drawing.Rectangle $sx, $sy, $side, $side),
                [System.Drawing.GraphicsUnit]::Pixel)
            $graphics.Dispose()
            $canvas.Save((Join-Path $icons "$id.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            $canvas.Dispose()
        }
        finally {
            $image.Dispose()
        }
    }
    else {
        Write-Warning "Portrait missing for $id"
    }

    $model = Join-Path $models "$id.glb"
    if (Test-Path $model) {
        Copy-Item $model (Join-Path $modelDir "$id.glb") -Force
    }
    else {
        Write-Warning "Model missing: $model"
    }
}

# Starter weapons (static GLBs, grip at the origin, same space as the class
# models). classes.json names the one (or two) each class holds.
$weapons = Join-Path $root 'art\game\weapons'
if (Test-Path $weapons) {
    Get-ChildItem $weapons -Filter *.glb | Copy-Item -Destination $weaponDir -Force
}
else {
    Write-Warning "Weapon folder missing: $weapons"
}

# PoE effect meshes for skills (Fireball core, Ice Shot arrow, Arc ribbon).
# Built by `node mod/tools/poe-gltf/build-glb.mjs --effects`.
$effects = Join-Path $root 'art\game\effects'
if (Test-Path $effects) {
    Get-ChildItem $effects -Filter *.glb | Copy-Item -Destination $effectDir -Force
}
else {
    Write-Warning "Effect folder missing: $effects"
}

# Enemy creatures for the Megabonk bestiary (PoeEnemies maps EEnemy -> one of
# these families). Same GLB pipeline as the class models.
$creatures = 'beasts', 'wolves', 'spiders', 'skeletons', 'zombies', 'ghosts', 'goatman',
    'stonegolem', 'fireelemental', 'tormentedspirit', 'bats', 'elder', 'shaper', 'maven',
    'searing-exarch', 'eater-of-worlds', 'merchants'
foreach ($creature in $creatures) {
    $model = Join-Path $models "$creature.glb"
    if (Test-Path $model) {
        Copy-Item $model (Join-Path $modelDir "$creature.glb") -Force
    }
    else {
        Write-Warning "Creature model missing: $model"
    }
}

$count = (Get-ChildItem $modelDir -Filter *.glb).Count
$weaponCount = (Get-ChildItem $weaponDir -Filter *.glb).Count
$iconsCount = (Get-ChildItem $icons -Filter *.png).Count
Write-Host "Prepared $($classes.Count) classes: $iconsCount icon(s), $count model(s), $weaponCount weapon(s), $propsShipped prop(s), $soundFiles sound(s) in $target"

