using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using SqlPhanos.CodeFormatting;
using SqlPhanos.Enums;
using SqlPhanos.QueryXLerator;
using SqlPhanos.Services;
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
public partial class QueryXLeratorDocumentViewModel : Document, IHasTabHeaderLines
{
	private readonly SqlCanonicalizationService _sqlCanonicalizationService = new();
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
	[NotifyPropertyChangedFor(nameof(TabIconState))]
	[NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
	[NotifyCanExecuteChangedFor(nameof(CancelCommand))]
	private bool _isRunning;

	[ObservableProperty]
	private string _status = "Ready.";

	[ObservableProperty]
	private string _durationText = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(TabIconState))]
	private bool _isInErrorState;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsContentInteractive))]
	private bool _showOverwriteConfirmation;

	// Set when SqlCanonicalizationService's own round-trip safety check rejected the formatted
	// output and fell back to returning QueryText unchanged - see IsRoundTripSafe. Must be
	// surfaced rather than silently doing nothing, or the user has no way to know Reformat
	// didn't actually do anything.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsContentInteractive))]
	private bool _pendingFormattingSafetyWarning;

	public bool IsContentInteractive => !ShowOverwriteConfirmation && !PendingFormattingSafetyWarning;

	public string ConnectionDisplayName { get; }

	public List<string> TableStyleNames { get; } = DataTape.TableStyleNames().ToList();

	public bool CanRunQuery => !IsRunning;

	public string SyntaxScopeName => "source.sql";

	// IHasTabHeaderLines - see SqlDocumentViewModel's identical implementation for why.
	public string TabHeaderLine1 => ConnectionDisplayName;

	public string TabHeaderLine2 => "Ad-hoc Query";

	public DocumentTabIconState TabIconState =>
		IsRunning ? DocumentTabIconState.Busy :
		IsInErrorState ? DocumentTabIconState.Error :
		DocumentTabIconState.None;

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

	[RelayCommand]
	private void Reformat()
	{
		if (string.IsNullOrWhiteSpace(QueryText))
		{
			return;
		}

		var result = _sqlCanonicalizationService.FormatForDisplayWithPositions(QueryText, FormattingSettingsService.OpeningParenOnNewLine);
		QueryText = result.Text;
		PendingFormattingSafetyWarning = !result.SafetyCheckPassed;
	}

	[RelayCommand]
	private void DismissFormattingSafetyWarning()
	{
		PendingFormattingSafetyWarning = false;
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
