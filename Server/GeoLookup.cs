using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
 
/// <summary>
/// Finds the country of an IP address using the free GeoLite2 file.
/// When you replace the file with a newer one, it is picked up automatically
/// (checked every 10 minutes), so you do not need to restart the server.
/// </summary>
public sealed class GeoLookup
{
    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private DatabaseReader? _reader;
    private DateTime _loadedFileTime;
    private DateTime _nextCheck = DateTime.MinValue;
 
    public GeoLookup(string path, ILogger log)
    {
        _path = path;
        _log = log;
        Reload();
    }
 
    /// <summary>True when the database file was loaded successfully.</summary>
    public bool IsReady => _reader is not null;
 
    /// <summary>Returns a 2-letter country code like "IN", or null if unknown.</summary>
    public string? GetCountry(IPAddress ip)
    {
        CheckForNewFile();
 
        var reader = _reader;
        if (reader is null) return null;
 
        // Some servers show IPv4 as ::ffff:1.2.3.4. Convert it back to normal.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
 
        try
        {
            return reader.TryCountry(ip, out var response)
                ? response?.Country.IsoCode?.ToUpperInvariant()
                : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GeoIP lookup failed");
            return null;
        }
    }
 
    private void CheckForNewFile()
    {
        if (DateTime.UtcNow < _nextCheck) return;
 
        lock (_lock)
        {
            if (DateTime.UtcNow < _nextCheck) return;
            _nextCheck = DateTime.UtcNow.AddMinutes(10);
 
            try
            {
                if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _loadedFileTime)
                    Reload();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not check the GeoIP file");
            }
        }
    }
 
    private void Reload()
    {
        if (!File.Exists(_path))
        {
            _log.LogError("GeoLite2 file not found: {File}", _path);
            return;
        }
 
        try
        {
            var fileTime = File.GetLastWriteTimeUtc(_path);
 
            // Memory mode loads the whole file, so Windows does not lock it
            // and you can replace it while the server is running.
            var fresh = new DatabaseReader(_path, FileAccessMode.Memory);
 
            _reader = fresh;
            _loadedFileTime = fileTime;
            _log.LogInformation("GeoIP database loaded (file date {Date:yyyy-MM-dd})", fileTime);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load the GeoIP database. Keeping the old one.");
        }
    }
}
