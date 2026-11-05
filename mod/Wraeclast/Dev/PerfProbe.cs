using System.Diagnostics;
using Assets.Scripts.Actors.Player;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Dev;

/// <summary>
/// Frame-time readout for the conversion, logged every few seconds while a run
/// is live. Reports the game's own frame time plus how much of it the mod spends
/// evaluating bone clips and, on the CPU skin fallback, deforming and uploading
/// the vertices, so a slow run can be attributed instead of guessed at. Clip
/// sampling alone never explained a boss-approach drop: the CPU skin pass is a
/// separate, much larger cost and is timed here as its own number.
/// </summary>
internal static class PerfProbe
{
    private const float Window = 5f;

    private static float frameSeconds;
    private static int frames;
    private static float elapsed;
    private static long sampleCount;
    private static long sampleTicks;
    private static long skinCount;
    private static long skinTicks;

    internal static long BeginSample() => Stopwatch.GetTimestamp();

    internal static void EndSample(long start)
    {
        sampleCount++;
        sampleTicks += Stopwatch.GetTimestamp() - start;
    }

    /// <summary>Times one CPU skin pass (<c>GltfSkin.ApplySkin</c>).</summary>
    internal static long BeginSkin() => Stopwatch.GetTimestamp();

    internal static void EndSkin(long start)
    {
        skinCount++;
        skinTicks += Stopwatch.GetTimestamp() - start;
    }

    internal static void Tick(MyPlayer player)
    {
        if (!Plugin.LogFrameTime.Value || player == null)
        {
            return;
        }

        var delta = Time.unscaledDeltaTime;
        frameSeconds += delta;
        frames++;
        elapsed += delta;
        if (elapsed < Window)
        {
            return;
        }

        var frameMs = frames > 0 ? frameSeconds / frames * 1000f : 0f;
        var sampleMs = sampleTicks * 1000.0 / Stopwatch.Frequency;
        var skinMs = skinTicks * 1000.0 / Stopwatch.Frequency;
        Plugin.Logger.LogInfo(
            $"frame {frameMs:0.0} ms ({1000f / Mathf.Max(frameMs, 0.01f):0} fps), " +
            $"models {PoeEnemies.FullCount}/{PoeEnemies.LiveCount} full-rate, " +
            $"alive {EnemyCap.Count}/{Plugin.MaxAliveEnemies.Value}, " +
            $"bone samples {sampleCount / elapsed:0}/s, " +
            $"clip cost {sampleMs / Mathf.Max(frames, 1):0.00} ms/frame, " +
            $"skin {skinCount / elapsed:0}/s, skin cost {skinMs / Mathf.Max(frames, 1):0.00} ms/frame");

        frameSeconds = 0f;
        frames = 0;
        elapsed = 0f;
        sampleCount = 0;
        sampleTicks = 0;
        skinCount = 0;
        skinTicks = 0;
    }
}
