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
		if (!File.Exists(_filePath))
		{
			return Array.Empty<SqlConnectionViewModel>();
		}

		var json = File.ReadAllText(_filePath);
		if (string.IsNullOrWhiteSpace(json))
		{
			return Array.Empty<SqlConnectionViewModel>();
		}

		var store = JsonSerializer.Deserialize<ConnectionProfileStore>(json, SerializerOptions);
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
		var directoryPath = Path.GetDirectoryName(_filePath);
		if (!string.IsNullOrWhiteSpace(directoryPath))
		{
			Directory.CreateDirectory(directoryPath);
		}

		var store = new ConnectionProfileStore
		{
			Connections = connections
				.Where(connection => !string.IsNullOrWhiteSpace(connection.ServerAndInstance))
				.Select(connection => new ConnectionProfile
				{
					ServerAndInstance = connection.ServerAndInstance,
					UseWindowsAuth = connection.UseWindowsAuth,
					UserName = connection.UserName,
					TrustServerCertificate = connection.TrustServerCertificate
				})
				.ToList()
		};

		var json = JsonSerializer.Serialize(store, SerializerOptions);
		File.WriteAllText(_filePath, json);
	}
}
