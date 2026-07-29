using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SqlPhanos.Services;

public sealed record UpdateInfo(string Version, string ReleaseNotes, string DownloadUrl);

/// <summary>
/// Ambient holder for "is a newer SqlPhanos release available", following the same
/// static-service pattern as FormattingSettingsService/FontSettingsService (no DI container
/// exists in this app). Checks once on startup and then once an hour for as long as the app
/// stays open - releases only happen when a version tag is deliberately pushed (see the release
/// job in dotnet-desktop.yml), so there's no need to poll more aggressively than that.
///
/// UpdateAvailableChanged fires on a thread-pool thread (from the underlying Timer), not the UI
/// thread - subscribers that touch Avalonia controls must marshal back via Dispatcher.UIThread.
/// </summary>
public static class UpdateCheckService
{
	private const string ReleasesApiUrl = "https://api.github.com/repos/larrye11ison/SqlTools/releases";
	private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
	private static readonly HttpClient Client = CreateHttpClient();
	private static Timer? _timer;

	public static UpdateInfo? AvailableUpdate { get; private set; }

	public static event EventHandler? UpdateAvailableChanged;

	public static void Initialize()
	{
		_timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.Zero, CheckInterval);
	}

	private static HttpClient CreateHttpClient()
	{
		var client = new HttpClient();
		// GitHub's REST API rejects unauthenticated requests with no User-Agent header.
		client.DefaultRequestHeaders.UserAgent.ParseAdd("SqlPhanos-UpdateCheck");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return client;
	}

	private static async Task CheckAsync()
	{
		try
		{
			using var stream = await Client.GetStreamAsync(ReleasesApiUrl);
			var releases = await JsonSerializer.DeserializeAsync<GitHubRelease[]>(stream);

			// The list endpoint (not /releases/latest) is used deliberately - GitHub returns it
			// newest-first and it includes prereleases, so a "-beta" tag is picked up the same
			// as a plain vX.Y.Z one instead of being silently skipped.
			var latest = releases?.FirstOrDefault(r => !r.Draft);
			if (latest is null)
			{
				return;
			}

			var latestVersion = latest.TagName.TrimStart('v', 'V');
			var currentVersion = AppVersionService.GetDisplayVersion();

			if (!IsNewer(latestVersion, currentVersion))
			{
				return;
			}

			var asset = latest.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
			if (asset is null)
			{
				return;
			}

			SetAvailableUpdate(new UpdateInfo(latestVersion, latest.Body ?? "", asset.BrowserDownloadUrl));
		}
		catch (Exception ex)
		{
			// Best-effort only - a failed check (offline, GitHub rate limit, etc.) just leaves
			// the update menu item hidden; nothing here should ever disrupt normal app usage.
			System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
		}
	}

	private static void SetAvailableUpdate(UpdateInfo info)
	{
		if (AvailableUpdate?.Version == info.Version)
		{
			return;
		}

		AvailableUpdate = info;
		UpdateAvailableChanged?.Invoke(null, EventArgs.Empty);
	}

	// Real releases are deliberately, monotonically versioned (MinVer + manually pushed tags),
	// so a simple three-part numeric comparison is enough here - no need for full
	// prerelease-aware semver parsing/ordering for this.
	private static bool IsNewer(string latest, string current)
	{
		return ParseCore(latest).CompareTo(ParseCore(current)) > 0;
	}

	private static (int Major, int Minor, int Patch) ParseCore(string version)
	{
		var core = version.Split('-', '+')[0];
		var parts = core.Split('.');

		int Part(int index) => index < parts.Length && int.TryParse(parts[index], out var value) ? value : 0;

		return (Part(0), Part(1), Part(2));
	}

	private sealed class GitHubRelease
	{
		[JsonPropertyName("tag_name")]
		public string TagName { get; set; } = "";

		[JsonPropertyName("body")]
		public string? Body { get; set; }

		[JsonPropertyName("draft")]
		public bool Draft { get; set; }

		[JsonPropertyName("assets")]
		public GitHubReleaseAsset[] Assets { get; set; } = Array.Empty<GitHubReleaseAsset>();
	}

	private sealed class GitHubReleaseAsset
	{
		[JsonPropertyName("name")]
		public string Name { get; set; } = "";

		[JsonPropertyName("browser_download_url")]
		public string BrowserDownloadUrl { get; set; } = "";
	}
}
