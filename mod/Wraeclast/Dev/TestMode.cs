using System;
using System.Collections.Generic;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Utility;
using UnityEngine;

namespace Wraeclast.Dev;

/// <summary>
/// F1 test mode (<c>Debug.RunTestKeys</c>, off by default): the player takes no
/// damage and walks through everything. One toggle owns both, because inspecting
/// a boss without dying and without a wall in the way is a single gesture.
///
/// Invincibility rides the same switch the gallery uses: the damage prefixes
/// (<c>Patches/GalleryPatches.cs</c>) skip <c>PlayerHealth.DamagePlayer</c>,
/// <c>DamagePlayerExternal</c> and <c>KillPlayer</c> while this is on. No clip
/// turns the player's own colliders into triggers and drops gravity, so the
/// character keeps the game's movement but no longer collides with the world;
/// <see cref="Sustain"/> pins the vertical velocity each frame, so a game-side
/// gravity write cannot sink it.
/// </summary>
internal static class TestMode
{
    internal static bool Invincible { get; private set; }

    internal static bool NoClip { get; private set; }

    /// <summary>Set for the one frame of the boss window's deadline kill: the
    /// floor rule must still end the run while F1 is on (a test that silently
    /// does nothing is worse than a death), but ordinary combat damage and
    /// clicks stay ignored.</summary>
    internal static bool BypassInvincibility { get; set; }

    /// <summary>Flight speed of the no-clip move, metres per second (the run is
    /// about 12; this is fast enough to cross a floor in seconds).</summary>
    private const float Speed = 26f;

    private static readonly List<Collider> Disabled = new List<Collider>();
    private static Rigidbody body;
    private static bool gravity = true;

    internal static void Toggle(MyPlayer player)
    {
        Invincible = !Invincible;
        NoClip = Invincible;

        try
        {
            if (Invincible)
            {
                Disabled.Clear();
                var colliders = player.GetComponentsInChildren<Collider>(true);
                foreach (var collider in colliders)
                {
                    if (collider != null && !collider.isTrigger)
                    {
                        Disabled.Add(collider);
                        collider.isTrigger = true;
                    }
                }

                body = player.GetComponent<Rigidbody>();
                if (body == null)
                {
                    body = player.GetComponentInChildren<Rigidbody>();
                }

                if (body != null)
                {
                    gravity = body.useGravity;
                    body.useGravity = false;
                }

                Plugin.Logger.LogInfo(
                    $"test mode on: invincible, no clip ({Disabled.Count} collider(s) as triggers, " +
                    $"gravity {(body != null ? "off" : "not found")})");
            }
            else
            {
                foreach (var collider in Disabled)
                {
                    if (collider != null)
                    {
                        collider.isTrigger = false;
                    }
                }

                Disabled.Clear();
                if (body != null)
                {
                    body.useGravity = gravity;
                }

                body = null;
                Plugin.Logger.LogInfo("test mode off: damage and collisions back");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"test mode: {error.Message}");
        }
    }

    /// <summary>Held while no clip is on: the camera-relative WASD move plus
    /// Space up / Ctrl down, at a faster-than-run speed, written straight to the
    /// transform. The rigidbody velocity is zeroed first, so the game's own
    /// controller cannot pull the player back and there is no drift.</summary>
    internal static void Sustain(MyPlayer player)
    {
        if (!NoClip || player == null || player.transform == null)
        {
            return;
        }

        try
        {
            if (body != null && !body.isKinematic)
            {
                body.velocity = Vector3.zero;
            }

            var camera = Camera.main;
            var forward = camera != null ? camera.transform.forward : player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward).normalized;
            // ZQSD (AZERTY) plus WASD and the arrows, so the test move works on
            // any layout the game itself accepts.
            var strafe = Axis(KeyCode.D) + Axis(KeyCode.RightArrow) - Axis(KeyCode.Q) - Axis(KeyCode.A) - Axis(KeyCode.LeftArrow);
            var advance = Axis(KeyCode.Z) + Axis(KeyCode.W) + Axis(KeyCode.UpArrow) - Axis(KeyCode.S) - Axis(KeyCode.DownArrow);
            var direction = right * strafe + forward * advance;

            if (direction.sqrMagnitude > 1f)
            {
                direction.Normalize();
            }

            var vertical = 0f;
            if (Input.GetKey(KeyCode.Space))
            {
                vertical += 1f;
            }

            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            {
                vertical -= 1f;
            }

            direction += Vector3.up * vertical;
            player.transform.position += direction * (Speed * MyTime.deltaTime);
        }
        catch (Exception)
        {
            // A destroyed player during a scene change; the next toggle re-arms.
        }
    }

    private static float Axis(KeyCode key) => Input.GetKey(key) ? 1f : 0f;

    internal static void Reset()
    {
        Invincible = false;
        NoClip = false;
        Disabled.Clear();
        body = null;
    }
}
