using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.SqlClient;

namespace SqlPhanos.ViewModels;

public partial class SqlConnectionViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsingWindowsAuth))]
    private string _password = "";

    [ObservableProperty]
    private string _serverAndInstance = "";

    [ObservableProperty]
    private bool _trustServerCertificate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsingWindowsAuth))]
    private string _userName = "";

    [ObservableProperty]
    private bool _useWindowsAuth = true;

    public string ConnectionString
    {
        get
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = ServerAndInstance,
                ConnectTimeout = 30,
                InitialCatalog = "master",
                TrustServerCertificate = TrustServerCertificate
            };

            if (UseWindowsAuth)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = UserName;
                builder.Password = Password;
                builder.IntegratedSecurity = false;
            }
            return builder.ConnectionString;
        }
    }

    public bool IsUsingWindowsAuth => UseWindowsAuth;

    public override string ToString()
    {
        return ServerAndInstance;
    }
}