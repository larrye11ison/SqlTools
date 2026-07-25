using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class DatabaseListItemTests
{
    [Fact]
    public void ReportProgress_AfterComplete_DoesNotOverwriteStatus()
    {
        DatabaseListItem item = new("Ashlin", isSelected: true);
        item.BeginScripting();
        item.ReportProgress(100, "5 / 5 completed, 0 parallel.");
        item.CompleteScripting();

        item.ReportProgress(100, "5 / 5 completed, 0 parallel.");

        Assert.Equal("Complete.", item.ProgressStatus);
        Assert.False(item.IsBusy);
    }

    [Fact]
    public void CancelScripting_ClearsBusyAndSetsCancelledStatus()
    {
        DatabaseListItem item = new("Ashlin", isSelected: true);
        item.BeginScripting();

        item.CancelScripting();

        Assert.False(item.IsBusy);
        Assert.Equal("Cancelled.", item.ProgressStatus);
    }
}
