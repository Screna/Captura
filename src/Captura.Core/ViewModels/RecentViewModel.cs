﻿using Captura.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Newtonsoft.Json;

namespace Captura.ViewModels
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public class RecentViewModel : ViewModelBase, IRecentList
    {
        readonly ObservableCollection<IRecentItem> _recentList = new ObservableCollection<IRecentItem>();

        public ReadOnlyObservableCollection<IRecentItem> RecentList { get; }
        
        public ICommand ClearCommand { get; }
        
        readonly Settings _settings;

        static string GetFilePath()
        {
            return Path.Combine(ServiceProvider.SettingsDir, "RecentItems.json");
        }

        public RecentViewModel(Settings Settings, LanguageManager LanguageManager) : base(Settings, LanguageManager)
        {
            RecentList = new ReadOnlyObservableCollection<IRecentItem>(_recentList);

            _settings = Settings;

            Load();

            ClearCommand = new DelegateCommand(Clear);
        }

        void Load()
        {
            try
            {
                //var json = File.ReadAllText(GetFilePath());

                //var list = JsonConvert.DeserializeObject<RecentItemModel[]>(json)
                //    .Reverse() // Reversion required to maintain order
                //    .Where(M => M.ItemType == RecentItemType.Link ||
                //                File.Exists(M.FilePath)); // Restore only if file exists

                //foreach (var model in list)
                //{
                //    var item = Add(model.FilePath, model.ItemType, false);
                //    item.DeleteHash = model.DeleteHash;
                //}
            }
            catch
            {
                // Ignore Errors
            }
        }

        public void Add(IRecentItem RecentItem)
        {
            // Insert on Top
            _recentList.Insert(0, RecentItem);

            RecentItem.RemoveRequested += () => _recentList.Remove(RecentItem);
        }

        IEnumerable<IRecentItem> IRecentList.Items => RecentList;

        public void Clear()
        {
            _recentList.Clear();
        }

        public void Dispose()
        {
            // Persist only if File exists or is a link.
            //var items = RecentList.Where(M => M.ItemType == RecentItemType.Link && !M.IsSaving || File.Exists(M.FilePath))
            //    .Select(M => new RecentItemModel(M.FilePath, M.ItemType, M.DeleteHash))
            //    .Take(_settings.RecentMax);

            //try
            //{
            //    var json = JsonConvert.SerializeObject(items, Formatting.Indented, new JsonSerializerSettings
            //    {
            //        NullValueHandling = NullValueHandling.Ignore
            //    });

            //    File.WriteAllText(GetFilePath(), json);
            //}
            //catch
            //{
            //    // Ignore Errors
            //}
        }
    }
}