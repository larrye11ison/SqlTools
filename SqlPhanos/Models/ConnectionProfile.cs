namespace SqlPhanos.Models;

public sealed class ConnectionProfile
{
	public string ServerAndInstance { get; set; } = "";

	public bool UseWindowsAuth { get; set; } = true;

	public string UserName { get; set; } = "";

	public bool TrustServerCertificate { get; set; } = true;
}
