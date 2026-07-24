using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.QueryXLerator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SqlPhanos.ViewModels;

/// <summary>
/// A single ad-hoc "run this SQL command against this connection and write the result set(s)
/// to an XLSX file" session, ported from QueryXLerator's FileGenerationTaskViewModel (state
/// model reused, mechanism rewritten to use CommunityToolkit.Mvvm like every other ViewModel
/// in this app instead of the original's hand-rolled INotifyPropertyChanged).
/// </summary>
public partial class QueryXLeratorDocumentViewModel : Document
{
	private readonly string _connectionString;

	private CancellationTokenSource? _cancellationTokenSource;

	[ObservableProperty]
	private string _queryText = "";

	[ObservableProperty]
	private string _outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Query.xlsx");

	[ObservableProperty]
	private bool _includeEmptyResultSets;

	[ObservableProperty]
	private string? _selectedTableStyleName = "None";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanRunQuery))]
	[NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
	[NotifyCanExecuteChangedFor(nameof(CancelCommand))]
	private bool _isRunning;

	[ObservableProperty]
	private string _status = "Ready.";

	[ObservableProperty]
	private string _durationText = "";

	[ObservableProperty]
	private bool _isInErrorState;

	[ObservableProperty]
	private bool _showOverwriteConfirmation;

	public string ConnectionDisplayName { get; }

	public List<string> TableStyleNames { get; } = DataTape.TableStyleNames().ToList();

	public bool CanRunQuery => !IsRunning;

	public string SyntaxScopeName => "source.sql";

	// Parameterless constructor exists only for the XAML Design.DataContext tag, matching the
	// same pattern SqlDocumentViewModel already uses for the same reason.
	public QueryXLeratorDocumentViewModel()
	{
		_connectionString = "";
		ConnectionDisplayName = "";
		Title = "Query";
	}

	public QueryXLeratorDocumentViewModel(string connectionString, string connectionDisplayName)
	{
		_connectionString = connectionString;
		ConnectionDisplayName = connectionDisplayName;
		Title = $"Query - {connectionDisplayName}";
	}

	[RelayCommand(CanExecute = nameof(CanRunQuery))]
	private async Task ExecuteAsync()
	{
		if (string.IsNullOrWhiteSpace(QueryText))
		{
			Status = "Enter a query first.";
			return;
		}

		if (string.IsNullOrWhiteSpace(OutputPath))
		{
			Status = "Choose an output file first.";
			return;
		}

		if (!OutputPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
		{
			OutputPath += ".xlsx";
		}

		if (File.Exists(OutputPath))
		{
			ShowOverwriteConfirmation = true;
			return;
		}

		await RunAsync();
	}

	[RelayCommand]
	private void CancelOverwrite()
	{
		ShowOverwriteConfirmation = false;
	}

	[RelayCommand]
	private async Task ConfirmOverwriteAsync()
	{
		ShowOverwriteConfirmation = false;
		await RunAsync();
	}

	[RelayCommand(CanExecute = nameof(IsRunning))]
	private void Cancel()
	{
		_cancellationTokenSource?.Cancel();
	}

	private async Task RunAsync()
	{
		IsRunning = true;
		IsInErrorState = false;
		Status = "Running...";
		DurationText = "";

		var started = DateTime.Now;
		var cts = new CancellationTokenSource();
		_cancellationTokenSource = cts;

		using var elapsedTimer = new System.Timers.Timer(1000);
		elapsedTimer.Elapsed += (_, _) =>
		{
			var elapsed = DateTime.Now.Subtract(started).ToString(@"hh\:mm\:ss");
			Dispatcher.UIThread.Post(() => DurationText = elapsed);
		};
		elapsedTimer.Start();

		try
		{
			var query = QueryText;
			var output = OutputPath;
			var includeEmpty = IncludeEmptyResultSets;
			var tableStyle = SelectedTableStyleName ?? "";
			var connectionString = _connectionString;

			await Task.Run(() => DataTape.WriteOutputFile(output, query, connectionString, includeEmpty, tableStyle, cts.Token));
			Status = "Complete.";
		}
		catch (Exception ex)
		{
			// Cancelling mid-query cancels the underlying SqlCommand, which surfaces as a
			// plain exception from ExecuteReader/Read - not a clean OperationCanceledException
			// - so cancellation is detected via the token, matching how the original app's
			// FileGenerationTaskViewModel.Run distinguished "cancelled" from "genuine error".
			if (cts.IsCancellationRequested)
			{
				Status = "Cancelled.";
			}
			else
			{
				IsInErrorState = true;
				Status = $"FAIL: {ex.Message}";
			}
		}
		finally
		{
			elapsedTimer.Stop();
			_cancellationTokenSource = null;
			IsRunning = false;
		}
	}
}
