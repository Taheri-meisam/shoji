using System;
using System.Collections.Generic;

namespace Shoji.Core;

/// <summary>
/// Deck icon names (mic, play, game…) and the Material Icons glyph each stands for. The Linux dashboard draws them
/// with that font, and the phone page uses it for the deck buttons on both systems.
/// </summary>
public static class DeckIcons
{
    public static readonly IReadOnlyDictionary<string, string> Material = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mic"] = "mic", ["micoff"] = "mic_off", ["mute"] = "volume_off", ["volume"] = "volume_up", ["voldown"] = "volume_down",
        ["speaker"] = "speaker", ["headphones"] = "headphones", ["play"] = "play_arrow", ["pause"] = "pause",
        ["next"] = "skip_next", ["prev"] = "skip_previous", ["camera"] = "photo_camera", ["lock"] = "lock", ["power"] = "power_settings_new",
        ["sleep"] = "bedtime", ["settings"] = "settings", ["web"] = "public", ["folder"] = "folder", ["terminal"] = "terminal",
        ["game"] = "sports_esports", ["music"] = "music_note", ["light"] = "lightbulb", ["home"] = "home", ["pages"] = "view_carousel",
        ["theme"] = "palette", ["timer"] = "timer", ["chat"] = "chat", ["calc"] = "calculate", ["chart"] = "insert_chart",
        ["mail"] = "mail", ["calendar"] = "calendar_month", ["clock"] = "schedule", ["star"] = "star", ["heart"] = "favorite",
        ["refresh"] = "refresh", ["download"] = "download", ["code"] = "code", ["video"] = "videocam", ["record"] = "fiber_manual_record",
        ["scenes"] = "auto_awesome", ["brightness"] = "brightness_6", ["bolt"] = "bolt", ["keyboard"] = "keyboard", ["app"] = "apps",
        ["live"] = "sensors",
    };

    /// <summary>The Material glyph for an icon name, or null when the icon is an emoji / text.</summary>
    public static string? Glyph(string? icon) => Material.TryGetValue((icon ?? "").Trim(), out var g) ? g : null;
}
