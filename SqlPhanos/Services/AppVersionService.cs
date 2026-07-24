using System.Reflection;

namespace SqlPhanos.Services;

public static class AppVersionService
{
	public static string GetDisplayVersion()
	{
		var informational = Assembly.GetExecutingAssembly()
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion;

		if (string.IsNullOrWhiteSpace(informational))
		{
			return "0.0.0";
		}

		// MinVer appends "+<commit sha>" build metadata to the informational version;
		// that's noise for a title bar, so only the semver part before it is shown.
		var plusIndex = informational.IndexOf('+');
		return plusIndex >= 0 ? informational[..plusIndex] : informational;
	}
}
