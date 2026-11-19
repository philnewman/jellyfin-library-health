using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LibraryHealth.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LibraryHealth;

/// <summary>
/// Read-only health report for the music library. It lists metadata problems and never changes anything.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Library Health";

    public override string Description => "Lists metadata problems in your music library. It only reports; it never changes your library.";

    public override Guid Id => Guid.Parse("e9b98148-8743-4071-8b64-f774ec8813d0");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Library Health",
                EnableInMainMenu = true,
                MenuIcon = "health_and_safety",
                EmbeddedResourcePath = "Jellyfin.Plugin.LibraryHealth.Configuration.configPage.html"
            }
        };
    }
}
