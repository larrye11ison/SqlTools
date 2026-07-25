using System.Collections.Generic;

namespace SqlPhanos.Models;

public sealed class ConnectionProfileStore
{
	public List<ConnectionProfile> Connections { get; set; } = new();

	public string? FontFamily { get; set; }

	public double? FontSize { get; set; }

	public bool? OpeningParenOnNewLine { get; set; }
}
