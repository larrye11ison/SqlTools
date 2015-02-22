namespace SqlTools.UI
{
    public class SqlToolsWindowManager : Caliburn.Micro.WindowManager
    {
        protected override System.Windows.Window EnsureWindow(object model, object view, bool isDialog)
        {
            var w = base.EnsureWindow(model, view, isDialog);
            //w.RenderSize = new System.Windows.Size(1024, 768);
            return w;
        }
    }
}