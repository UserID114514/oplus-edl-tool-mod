using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OplusEdlTool.Services;

namespace OplusEdlTool
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Exit += OnApplicationExit;
                
                LanguageService.Initialize();
                
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }
        
        private void OnApplicationExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
        {
            try
            {
                var appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OplusEdlTool");
                
                if (Directory.Exists(appDataPath))
                {
                    var preserveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "language.json"
                    };
                    
                    foreach (var file in Directory.GetFiles(appDataPath))
                    {
                        var fileName = Path.GetFileName(file);
                        if (!preserveFiles.Contains(fileName))
                        {
                            try
                            {
                                File.Delete(file);
                            }
                            catch { }
                        }
                    }
                    
                    foreach (var dir in Directory.GetDirectories(appDataPath))
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}
