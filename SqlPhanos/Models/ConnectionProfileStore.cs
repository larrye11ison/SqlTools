using System.Collections.Generic;

namespace SqlPhanos.Models;

public sealed class ConnectionProfileStore
{
	public List<ConnectionProfile> Connections { get; set; } = new();
}
