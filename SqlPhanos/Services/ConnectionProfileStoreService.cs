using SqlPhanos.Models;
using SqlPhanos.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SqlPhanos.Services;

public sealed class ConnectionProfileStoreService
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true
	};

	private readonly string _filePath;

	public ConnectionProfileStoreService()
	{
		var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var directoryPath = Path.Combine(appDataPath, "SqlPhanos");
		_filePath = Path.Combine(directoryPath, "connections.json");
	}

	public IReadOnlyList<SqlConnectionViewModel> LoadConnections()
	{
		var store = LoadStore();
		if (store?.Connections is null || store.Connections.Count == 0)
		{
			return Array.Empty<SqlConnectionViewModel>();
		}

		return store.Connections
			.Where(connection => !string.IsNullOrWhiteSpace(connection.ServerAndInstance))
			.Select(connection => new SqlConnectionViewModel
			{
				ServerAndInstance = connection.ServerAndInstance,
				UseWindowsAuth = connection.UseWindowsAuth,
				UserName = connection.UserName,
				Password = string.Empty,
				TrustServerCertificate = connection.TrustServerCertificate
			})
			.ToList();
	}

	public void SaveConnections(IEnumerable<SqlConnectionViewModel> connections)
	{
		// Read-modify-write so an existing FontFamily setting isn't wiped out by a save
		// that only knows about connections.
		var store = LoadStore() ?? new ConnectionProfileStore();
		store.Connections = connections
			.Where(connection => !string.IsNullOrWhiteSpace(connection.ServerAndInstance))
			.Select(connection => new ConnectionProfile
			{
				ServerAndInstance = connection.ServerAndInstance,
				UseWindowsAuth = connection.UseWindowsAuth,
				UserName = connection.UserName,
				TrustServerCertificate = connection.TrustServerCertificate
			})
			.ToList();

		WriteStore(store);
	}

	public string? LoadFontFamily()
	{
		return LoadStore()?.FontFamily;
	}

	public void SaveFontFamily(string fontFamily)
	{
		// Read-modify-write so existing saved connections aren't wiped out by a save
		// that only knows about the font setting.
		var store = LoadStore() ?? new ConnectionProfileStore();
		store.FontFamily = fontFamily;
		WriteStore(store);
	}

	private ConnectionProfileStore? LoadStore()
	{
		if (!File.Exists(_filePath))
		{
			return null;
		}

		var json = File.ReadAllText(_filePath);
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		return JsonSerializer.Deserialize<ConnectionProfileStore>(json, SerializerOptions);
	}

	private void WriteStore(ConnectionProfileStore store)
	{
		var directoryPath = Path.GetDirectoryName(_filePath);
		if (!string.IsNullOrWhiteSpace(directoryPath))
		{
			Directory.CreateDirectory(directoryPath);
		}

		var json = JsonSerializer.Serialize(store, SerializerOptions);
		File.WriteAllText(_filePath, json);
	}
}
