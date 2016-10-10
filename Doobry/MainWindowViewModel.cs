﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Doobry.Infrastructure;
using Doobry.Settings;
using Dragablz;
using Dragablz.Dockablz;
using DynamicData.Kernel;

namespace Doobry
{
    public class MainWindowViewModel
    {
        private readonly IConnectionCache _connectionCache;
        private readonly IGeneralSettings _generalSettings;
        private readonly IInitialLayoutStructureProvider _initialLayoutStructureProvider;

        private static bool _isStartupInitiated;

        public MainWindowViewModel(IConnectionCache connectionCache, IGeneralSettings generalSettings, IInitialLayoutStructureProvider initialLayoutStructureProvider)
        {
            if (connectionCache == null) throw new ArgumentNullException(nameof(connectionCache));
            if (generalSettings == null) throw new ArgumentNullException(nameof(generalSettings));
            if (initialLayoutStructureProvider == null)
                throw new ArgumentNullException(nameof(initialLayoutStructureProvider));

            _connectionCache = connectionCache;
            _generalSettings = generalSettings;
            _initialLayoutStructureProvider = initialLayoutStructureProvider;

            StartupCommand = new Command(RunStartup);
            ShutDownCommand = new Command(o => RunShutdown());
            Tabs = new ObservableCollection<TabViewModel>();            
        }

        public ObservableCollection<TabViewModel> Tabs { get; }

        public ICommand StartupCommand { get; }

        public ICommand ShutDownCommand { get; }

        private void RunStartup(object sender)
        {
            if (_isStartupInitiated) return;
            _isStartupInitiated = true;

            //TODO assert this stuff, provide a fall back
            var senderDependencyObject = sender as DependencyObject;
            var mainWindow = Window.GetWindow(senderDependencyObject) as MainWindow;
            var rootTabControl = mainWindow.InitialTabablzControl;

            LayoutStructure layoutStructure;
            if (_initialLayoutStructureProvider.TryTake(out layoutStructure))
            {
                RestoreLayout(rootTabControl, layoutStructure, _connectionCache);
            }

            if (TabablzControl.GetLoadedInstances().SelectMany(tc => tc.Items.OfType<object>()).Any()) return;

            var tabViewModel = new TabViewModel(Guid.NewGuid(), _connectionCache);
            rootTabControl.AddToSource(tabViewModel);
            Tabs.Add(tabViewModel);
            TabablzControl.SelectItem(tabViewModel);
            tabViewModel.EditConnectionCommand.Execute(rootTabControl);
        }

        private void RestoreLayout(TabablzControl rootTabControl, LayoutStructure layoutStructure, IConnectionCache connectionCache)
        {
            //we only currently support a single window, can build on in future
            var layoutStructureWindow = layoutStructure.Windows.Single();
            
            var layoutStructureTabSets = layoutStructureWindow.TabSets.ToDictionary(tabSet => tabSet.Id);

            if (layoutStructureWindow.Branches.Any())
            {
                var branchIndex = layoutStructureWindow.Branches.ToDictionary(b => b.Id);
                var rootBranch = GetRoot(branchIndex);

                //do the nasty recursion to build the layout, populate the tabs after, keep it simple...
                foreach (var tuple in BuildLayout(rootTabControl, rootBranch, branchIndex))
                {
                    PopulateTabControl(tuple.Item2, layoutStructureTabSets[tuple.Item1]);
                }
            }
            else
            {
                PopulateTabControl(rootTabControl, layoutStructureTabSets.Values.First());
            }
        }

        private void PopulateTabControl(TabablzControl tabablzControl, LayoutStructureTabSet layoutStructureTabSet)
        {
            foreach (var tabItem in layoutStructureTabSet.TabItems)
            {
                Connection connection = null;
                if (tabItem.ConnectionId.HasValue)
                {
                    connection = _connectionCache.Get(tabItem.ConnectionId.Value).ValueOrDefault();
                }
                var tabViewModel = new TabViewModel(tabItem.Id, connection, _connectionCache);
                tabablzControl.AddToSource(tabViewModel);

                if (tabViewModel.Id == layoutStructureTabSet.SelectedTabItemId)
                {
                    tabablzControl.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        tabablzControl.SetCurrentValue(Selector.SelectedItemProperty, tabViewModel);
                    }), DispatcherPriority.Loaded);                    
                }
            }
        }

        public static readonly DependencyProperty TabSetIdProperty = DependencyProperty.RegisterAttached(
            "TabSetId", typeof(Guid?), typeof(MainWindowViewModel), new PropertyMetadata(default(Guid?)));

        public static void SetTabSetId(DependencyObject element, Guid? value)
        {
            element.SetValue(TabSetIdProperty, value);
        }

        public static Guid? GetTabSetId(DependencyObject element)
        {
            return (Guid?) element.GetValue(TabSetIdProperty);
        }

        private static IEnumerable<Tuple<Guid, TabablzControl>> BuildLayout(
            TabablzControl intoTabablzControl, 
            LayoutStructureBranch layoutStructureBranch,
            IDictionary<Guid, LayoutStructureBranch> layoutStructureBranchIndex)
        {            
            var newSiblingTabablzControl = CreateTabablzControl();
            var branchResult = Layout.Branch(intoTabablzControl, newSiblingTabablzControl, layoutStructureBranch.Orientation, false, layoutStructureBranch.Ratio);            

            if (layoutStructureBranch.ChildFirstBranchId.HasValue)
            {
                var firstChildBranch = layoutStructureBranchIndex[layoutStructureBranch.ChildFirstBranchId.Value];
                foreach (var tuple in BuildLayout(intoTabablzControl, firstChildBranch, layoutStructureBranchIndex))
                    yield return tuple;
            }
            else if (layoutStructureBranch.ChildFirstTabSetId.HasValue)
            {
                SetTabSetId(intoTabablzControl, layoutStructureBranch.ChildFirstTabSetId.Value);
                yield return new Tuple<Guid, TabablzControl>(layoutStructureBranch.ChildFirstTabSetId.Value, intoTabablzControl);
            }            

            if (layoutStructureBranch.ChildSecondBranchId.HasValue)
            {
                var secondChildBranch = layoutStructureBranchIndex[layoutStructureBranch.ChildSecondBranchId.Value];
                foreach (var tuple in BuildLayout(branchResult.TabablzControl, secondChildBranch, layoutStructureBranchIndex))
                    yield return tuple;
            }
            else if (layoutStructureBranch.ChildSecondTabSetId.HasValue)
            {
                SetTabSetId(newSiblingTabablzControl, layoutStructureBranch.ChildSecondTabSetId.Value);
                yield return new Tuple<Guid, TabablzControl>(layoutStructureBranch.ChildSecondTabSetId.Value, newSiblingTabablzControl);
            }         
        }

        private IEnumerable<Tuple<Guid, IList<TabViewModel>>> CreateTabItemSets(IEnumerable<LayoutStructureTabSet> tabSets)
        {
            return tabSets.Select(tabSet =>
                        new Tuple<Guid, IList<TabViewModel>>(tabSet.Id, CreateTabItemSet(tabSet))
            );
        }

        private IList<TabViewModel> CreateTabItemSet(LayoutStructureTabSet layoutStructureTabSet)
        {
            var result = new List<TabViewModel>();
            foreach (var layoutStructureTabItem in layoutStructureTabSet.TabItems)
            {
                Connection connection = null;
                if (layoutStructureTabItem.ConnectionId.HasValue)
                {
                    connection = _connectionCache.Get(layoutStructureTabItem.ConnectionId.Value).ValueOrDefault();
                }
                var tabViewModel = new TabViewModel(layoutStructureTabSet.Id, connection, _connectionCache);
                result.Add(tabViewModel);
            }            
            return result;
        }

        private static TabablzControl CreateTabablzControl()
        {
            return new TabablzControl();
        }        

        private static void BuildTabSet(LayoutStructureTabSet layoutStructureTabSet, TabablzControl intoTabablzControl, IConnectionCache connectionCache)
        {
            foreach (var layoutStructureTabItem in layoutStructureTabSet.TabItems)
            {                
                Connection connection = null;
                if (layoutStructureTabItem.ConnectionId.HasValue)
                {
                    connection = connectionCache.Get(layoutStructureTabItem.ConnectionId.Value).ValueOrDefault();
                }                
                var tabViewModel = new TabViewModel(layoutStructureTabSet.Id, connection, connectionCache);
                intoTabablzControl.AddToSource(tabViewModel);
                if (layoutStructureTabSet.SelectedTabItemId.HasValue &&
                    tabViewModel.Id == layoutStructureTabSet.SelectedTabItemId.Value)
                    intoTabablzControl.SetValue(Selector.SelectedItemProperty, tabViewModel);
            }
        }

        private static LayoutStructureBranch GetRoot(Dictionary<Guid, LayoutStructureBranch> branches)
        {
            var lookup = branches.Values.SelectMany(ChildBranchIds).Distinct().ToLookup(guid => guid);
            return branches.Values.Single(branch => !lookup.Contains(branch.Id));
        }

        private static IEnumerable<Guid> ChildBranchIds(LayoutStructureBranch branch)
        {
            if (branch.ChildFirstBranchId.HasValue)
                yield return branch.ChildFirstBranchId.Value;
            if (branch.ChildSecondBranchId.HasValue)
                yield return branch.ChildSecondBranchId.Value;
        }

        private void RunShutdown()
        {                        
            var windowCollection = Application.Current.Windows.OfType<MainWindow>();
            if (windowCollection.Count() == 1)
                RunApplicationShutdown();
        }

        private void RunApplicationShutdown()
        {
            new ManualSaver().Save(_connectionCache, _generalSettings);            

            Application.Current.Shutdown();
        }
    }    
}
