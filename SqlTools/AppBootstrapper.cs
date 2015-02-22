using Caliburn.Micro;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.Linq;
using System.Windows;

namespace SqlTools
{
    public class AppBootstrapper : BootstrapperBase, IDisposable
    {
        private CompositionContainer container;

        static AppBootstrapper()
        {
            LogManager.GetLog = type => new DebugLogger(type);
        }

        public AppBootstrapper()
            : base()
        {
            Initialize();
        }

        ~AppBootstrapper()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected override void BuildUp(object instance)
        {
            container.SatisfyImportsOnce(instance);
        }

        protected override void Configure()
        {
            container = new CompositionContainer(new AggregateCatalog(
                AssemblySource.Instance.Select(x => new AssemblyCatalog(x)).OfType<ComposablePartCatalog>()));

            var batch = new CompositionBatch();

            IWindowManager newWindowManager = new SqlTools.UI.SqlToolsWindowManager();
            batch.AddExportedValue<IWindowManager>(newWindowManager);
            EventAggregator newEventAggregator = new EventAggregator();
            batch.AddExportedValue<IEventAggregator>(newEventAggregator);
            batch.AddExportedValue(container);

            container.Compose(batch);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                if (container != null)
                {
                    container.Dispose();
                    container = null;
                }
        }

        protected override IEnumerable<object> GetAllInstances(Type serviceType)
        {
            return container.GetExportedValues<object>(AttributedModelServices.GetContractName(serviceType));
        }

        protected override object GetInstance(Type serviceType, string key)
        {
            var contract = string.IsNullOrEmpty(key) ? AttributedModelServices.GetContractName(serviceType) : key;
            var exports = container.GetExportedValues<object>(contract);

            if (exports.Any())
                return exports.First();

            throw new Exception(string.Format("Could not locate any instances of contract {0}.", contract));
        }

        protected override void OnStartup(object sender, StartupEventArgs e)
        {
            DisplayRootViewFor<IShell>();
        }
    }
}