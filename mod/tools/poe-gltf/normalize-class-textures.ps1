# Normalise the pale class textures to the Shadow's exposure with a per-class
# gamma curve. The Shadow (trickster) texture is the reference the operator
# considers correctly lit. Outputs <name>_lum.png beside the source.
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$base = Join-Path $root "art\raw\textures\characters\art\textures\characters"
$jobs = @(
  @{ file = "hunter\ranger_colour_dxt1.png"; gamma = 1.80 },
  @{ file = "witch\witch_colour_dxt1.png"; gamma = 1.70 },
  @{ file = "adventurer\adventurer_colour_dxt1.png"; gamma = 1.80 },
  @{ file = "templar\templar_colour_dxt1.png"; gamma = 2.40 }
)

foreach ($job in $jobs) {
  $source = Join-Path $base $job.file
  if (-not (Test-Path $source)) {
    Write-Host "Missing: $source"
    continue
  }
  $image = [System.Drawing.Bitmap]::FromFile($source)
  $output = New-Object System.Drawing.Bitmap $image.Width, $image.Height
  $gamma = [double]$job.gamma
  $lut = New-Object 'byte[]' 256
  for ($i = 0; $i -lt 256; $i++) {
    $lut[$i] = [byte][Math]::Round(255.0 * [Math]::Pow($i / 255.0, $gamma))
  }
  for ($y = 0; $y -lt $image.Height; $y++) {
    for ($x = 0; $x -lt $image.Width; $x++) {
      $c = $image.GetPixel($x, $y)
      $output.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c.A, $lut[$c.R], $lut[$c.G], $lut[$c.B]))
    }
  }
  $target = [System.IO.Path]::ChangeExtension($source, ".png")
  $target = $target -replace "\.png$", "_lum.png"
  $output.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
  $output.Dispose()
  $image.Dispose()
  Write-Host "Wrote $target (gamma $gamma)"
}
