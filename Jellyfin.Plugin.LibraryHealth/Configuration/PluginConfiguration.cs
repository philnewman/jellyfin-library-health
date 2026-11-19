using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryHealth.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Keys of findings marked "not a problem". They stay hidden in later scans.</summary>
    public List<string> DismissedKeys { get; set; } = new();

    /// <summary>Folder names that hold albums by many artists, so tracks in them aren't reported as misfiled.</summary>
    public string SharedFolderNames { get; set; } = "Various Artists, Various, Compilations, Soundtracks, Soundtrack";
}
