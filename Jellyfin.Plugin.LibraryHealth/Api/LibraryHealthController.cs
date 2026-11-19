using System;
using System.Linq;
using Jellyfin.Plugin.LibraryHealth.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryHealth.Api;

/// <summary>
/// Report endpoints for the dashboard page. All of them need an administrator.
/// None of them change the library; dismissing only updates this plugin's settings.
/// </summary>
[ApiController]
[Route("LibraryHealth")]
[Authorize(Policy = "RequiresElevation")]
public class LibraryHealthController : ControllerBase
{
    private readonly HealthScanner _scanner;

    public LibraryHealthController(HealthScanner scanner)
    {
        _scanner = scanner;
    }

    /// <summary>The last report (scanning first if there isn't one). Pass rescan=true to scan again.</summary>
    [HttpGet("Report")]
    public ActionResult<HealthReport> GetReport([FromQuery] bool rescan = false)
    {
        return _scanner.GetReport(rescan);
    }

    /// <summary>Marks a finding as "not a problem", or brings it back.</summary>
    [HttpPost("Dismissed")]
    public ActionResult SetDismissed([FromQuery] string key, [FromQuery] bool dismissed = true)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || string.IsNullOrWhiteSpace(key))
        {
            return BadRequest();
        }

        var keys = plugin.Configuration.DismissedKeys;
        keys.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
        if (dismissed)
        {
            keys.Add(key);
        }

        plugin.SaveConfiguration();

        foreach (var f in _scanner.GetReport(false).Findings.Where(f => f.Key == key))
        {
            f.Dismissed = dismissed;
        }

        return NoContent();
    }
}
