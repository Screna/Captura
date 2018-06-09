using System;
using Captura.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Input;
using Newtonsoft.Json;

namespace Captura
{
    public class HotKeyManager : IDisposable
    {
        readonly ObservableCollection<Hotkey> _hotkeys = new ObservableCollection<Hotkey>();

        public ReadOnlyObservableCollection<Hotkey> Hotkeys { get; }

        static string GetFilePath() => Path.Combine(ServiceProvider.SettingsDir, "Hotkeys.json");

        public ICommand ResetCommand { get; }

        static HotKeyManager()
        {
            for (var i = ServiceName.None; i < ServiceName.ServiceCount; ++i)
                AllServices.Add(new Service(i));
        }

        public HotKeyManager()
        {
            Hotkeys = new ReadOnlyObservableCollection<Hotkey>(_hotkeys);

            ResetCommand = new DelegateCommand(Reset);
        }

        public static List<Service> AllServices { get; } = new List<Service>();

        public void RegisterAll()
        {
            IEnumerable<HotkeyModel> models;

            try
            {
                var json = File.ReadAllText(GetFilePath());

                models = JsonConvert.DeserializeObject<IEnumerable<HotkeyModel>>(json);
            }
            catch
            {
                models = Defaults();
            }

            Populate(models);
        }

        public void Reset()
        {
            Dispose();

            _hotkeys.Clear();

            Populate(Defaults());
        }

        void Populate(IEnumerable<HotkeyModel> Models)
        {
            var nonReg = new List<Hotkey>();

            foreach (var model in Models)
            {
                var hotkey = new Hotkey(model);

                if (hotkey.IsActive && !hotkey.IsRegistered)
                    _notRegisteredOnStartup.Add(hotkey);

                _hotkeys.Add(hotkey);
            }
        }

        readonly List<Hotkey> _notRegisteredOnStartup = new List<Hotkey>();

        public void ShowNotRegisteredOnStartup()
        {
            if (_notRegisteredOnStartup.Count > 0)
            {
                var message = "The following Hotkeys could not be registered:\nOther programs might be using them.\nTry changing them.\n\n";

                foreach (var hotkey in _notRegisteredOnStartup)
                {
                    message += $"{hotkey.Service.Description} - {hotkey}\n\n";
                }

                ServiceProvider.MessageProvider.ShowError(message, "Failed to Register Hotkeys");
            }
        }

        static IEnumerable<HotkeyModel> Defaults()
        {
            yield return new HotkeyModel(ServiceName.Recording, Keys.F9, Modifiers.Alt, true);
            yield return new HotkeyModel(ServiceName.Pause, Keys.F9, Modifiers.Shift, true);
            yield return new HotkeyModel(ServiceName.ScreenShot, Keys.PrintScreen, 0, true);
            yield return new HotkeyModel(ServiceName.ActiveScreenShot, Keys.PrintScreen, Modifiers.Alt, true);
            yield return new HotkeyModel(ServiceName.DesktopScreenShot, Keys.PrintScreen, Modifiers.Shift, true);
            yield return new HotkeyModel(ServiceName.ToggleMouseClicks, Keys.F10, Modifiers.Alt, false);
            yield return new HotkeyModel(ServiceName.ToggleKeystrokes, Keys.F11, Modifiers.Alt, false);
        }
        
        public void ProcessHotkey(int Id)
        {
            var hotkey = Hotkeys.SingleOrDefault(H => H.ID == Id);

            if (hotkey != null)
                HotkeyPressed?.Invoke(hotkey.Service.ServiceName);
        }

        public event Action<ServiceName> HotkeyPressed;
        
        public void Dispose()
        {
            var models = Hotkeys.Select(M =>
            {
                M.Unregister();

                return new HotkeyModel(M.Service.ServiceName, M.Key, M.Modifiers, M.IsActive);
            });

            try
            {
                var json = JsonConvert.SerializeObject(models);

                File.WriteAllText(GetFilePath(), json);
            }
            catch
            {
                // Ignore Errors
            }
        }
    }
}