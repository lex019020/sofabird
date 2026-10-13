using Microsoft.Extensions.Logging;
using VlcRemote.Core.Vlc;

namespace VlcRemote.Companion.Vlc;

// Preparing VLC for phone control: vlcrc and the Lua pages.
public sealed partial class VlcManager
{
    // ------------------------------------------------------------------ vlcrc

    /// <summary>
    /// Write the web interface and RC with the companion's password into vlcrc (if <c>ConfigureVlc</c> is on).
    /// Only when no vlc.exe is running — otherwise VLC overwrites the file from memory.
    /// </summary>
    public VlcrcResult? TryConfigureVlcrc()
    {
        if (!_settings.ConfigureVlc || Installation is null) return null;
        try
        {
            if (VlcLocator.IsAnyRunning()) return null;
            return _vlcrc.Ensure(new VlcrcSettings(_settings.VlcHttpPort, _settings.VlcHttpPassword ?? "", _settings.VlcRcPort));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Configuring {Path} failed", _vlcrc.Path);
            return null;
        }
    }

    /// <summary>
    /// Read-only: VLC is set up for phone control (vlcrc has our interfaces and password, Lua pages are current).
    /// <c>null</c> when VLC is not installed.
    /// </summary>
    public bool? IsConfiguredForPhone()
    {
        if (Installation is null) return null;
        var rc = _vlcrc.IsConfigured(new VlcrcSettings(_settings.VlcHttpPort, _settings.VlcHttpPassword ?? "", _settings.VlcRcPort));
        bool lua;
        try { lua = _lua is null || !_lua.NeedsInstall(Installation.Version); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lua = false; }
        return rc && lua;
    }

    // ------------------------------------------------------------------ Lua pages

    /// <summary>Install the Lua pages if VLC is not running; otherwise postpone until it closes.</summary>
    public bool TryInstallLuaPages(bool force = false)
    {
        if (_lua is null || Installation is null) return false;
        try
        {
            if (!force && !_lua.NeedsInstall(Installation.Version))
            {
                _luaInstallPending = false;
                return false;
            }
            if (VlcLocator.IsAnyRunning())
            {
                if (!_luaInstallPending)
                    _log.LogInformation("Lua pages need (re)install but VLC is running; deferring until it exits");
                _luaInstallPending = true;
                return false;
            }
            _lua.Install(Installation.Version);
            _luaInstallPending = false;
            _tracksPageMissing = false;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lua pages install failed");
            _luaInstallPending = false;
            return false;
        }
    }
}
