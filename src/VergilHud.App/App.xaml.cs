using System.IO;
using System.Windows;

namespace VergilHud.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var early = AppSettings.Load();
        Theme.Light = early.Theme == "light";
        Loc.Language = early.Language;
        if (Environment.GetEnvironmentVariable("VERGIL_THEME") == "light") Theme.Light = true;
        if (Environment.GetEnvironmentVariable("VERGIL_LANG") == "en") Loc.Language = "en";
        Theme.ApplyResources(Resources);
        // Los tooltips de ayuda se quedan visibles el tiempo de leerlos
        System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(40000));
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(DependencyObject), new FrameworkPropertyMetadata(350));
        VergilHud.Core.AppLog.Init(AppSettings.LogsDir);
        DispatcherUnhandledException += (_, ex) =>
        {
            VergilHud.Core.AppLog.Error("Error no controlado en la interfaz", ex.Exception);
            MessageBox.Show("Ha ocurrido un error inesperado y se ha guardado en el registro (" + AppSettings.LogsDir + ")." + Environment.NewLine + Environment.NewLine +
                            ex.Exception.Message + Environment.NewLine + Environment.NewLine +
                            "Tus datos están a salvo. Puedes seguir usando la aplicación.", "Vergil HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => VergilHud.Core.AppLog.Error("Error fatal", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { VergilHud.Core.AppLog.Error("Error en segundo plano", ex.Exception); ex.SetObserved(); };

        // Modo de verificación: dibuja los paneles y popups a PNG sin abrir ninguna ventana.
        //   VergilHud.exe --render <hud.db> <carpeta-de-salida>
        if (e.Args.Length >= 3 && e.Args[0] == "--render")
        {
            try { DemoRenderer.Run(e.Args[1], e.Args[2]); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(e.Args[2]);
                File.WriteAllText(Path.Combine(e.Args[2], "error.txt"), ex.ToString());
            }
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--render-replayer")
        {
            try { ReplayerDemo.Render(e.Args[1], e.Args[2]); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(e.Args[2]);
                File.WriteAllText(Path.Combine(e.Args[2], "error.txt"), ex.ToString());
            }
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--selfcheck")
        {
            try { SelfCheck.Run(e.Args[1], e.Args[2]); }
            catch (Exception ex) { File.WriteAllText(e.Args[2], ex.ToString()); }
            Shutdown();
            return;
        }

        // VergilHud.exe --render-analysis <carpeta>: dibuja las vistas de análisis con datos de demostración.
        if (e.Args.Length >= 2 && e.Args[0] == "--render-analysis")
        {
            try { AnalysisDemo.Render(e.Args[1]); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(e.Args[1]);
                File.WriteAllText(Path.Combine(e.Args[1], "error.txt"), ex.ToString());
            }
            Shutdown();
            return;
        }

        // VergilHud.exe --view N: abre directamente la vista N del menú (0 = Dashboard … 6 = Ajustes).
        for (int i = 0; i + 1 < e.Args.Length; i++)
            if (e.Args[i] == "--view" && int.TryParse(e.Args[i + 1], out var v)) VergilHud.App.MainWindow.InitialView = v;

        new MainWindow().Show();
    }
}
