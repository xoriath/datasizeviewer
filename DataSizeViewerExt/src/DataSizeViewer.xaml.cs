﻿using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Atmel.Studio.Extensibility.Toolchain;
using Atmel.Studio.Services;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System.Text.RegularExpressions;

namespace FourWalledCubicle.DataSizeViewerExt
{
    public partial class DataSizeViewerUI : UserControl
    {
        private readonly DTE myDTE;

        private readonly IToolchainService mToolchainService;

        private BuildEvents mBuildEvents;
        private SolutionEvents mSolutionEvents;

        private SymbolSizeParser mSymbolParser;

        private bool mShowDataSegmentState = true;
        public bool mShowDataSegment
        {
            get
            {
                return mShowDataSegmentState;
            }

            set
            {
                mShowDataSegmentState = value;

                ICollectionView dataView = CollectionViewSource.GetDefaultView(mSymbolParser.symbolSizes);
                dataView.Refresh();
            }
        }

        private bool mShowTextSegmentState = true;
        public bool mShowTextSegment
        {
            get
            {
                return mShowTextSegmentState;
            }

            set
            {
                mShowTextSegmentState = value;

                ICollectionView dataView = CollectionViewSource.GetDefaultView(mSymbolParser.symbolSizes);
                dataView.Refresh();
            }
        }

        private string mFilterStringValue = string.Empty;
        public string mFilterString
        {
            get
            {
                return mFilterStringValue;
            }

            set
            {
                mFilterStringValue = value;

                ICollectionView dataView = CollectionViewSource.GetDefaultView(mSymbolParser.symbolSizes);
                dataView.Refresh();
            }
        }

        public DataSizeViewerUI()
        {
            InitializeComponent();

            myDTE = Package.GetGlobalService(typeof(DTE)) as DTE;

            mBuildEvents = myDTE.Events.BuildEvents;
            mBuildEvents.OnBuildDone += (Scope, Action) => ReloadProjectSymbols();

            mSolutionEvents = myDTE.Events.SolutionEvents;
            mSolutionEvents.Opened += () => UpdateProjectList();
            mSolutionEvents.AfterClosing += () => UpdateProjectList();
            mSolutionEvents.ProjectAdded += (p) => UpdateProjectList();
            mSolutionEvents.ProjectRemoved += (p) => UpdateProjectList();
            mSolutionEvents.ProjectRenamed += (p, s) => UpdateProjectList();

            projectList.SelectionChanged += (s, e) => ReloadProjectSymbols();

            mToolchainService = ATServiceProvider.ToolchainService;

            mSymbolParser = new SymbolSizeParser();
            symbolSize.ItemsSource = mSymbolParser.symbolSizes;
            ShowError("No symbols have been loaded. Recompile or click the refresh button.");

            ICollectionView dataView = CollectionViewSource.GetDefaultView(mSymbolParser.symbolSizes);
            dataView.GroupDescriptions.Add(new PropertyGroupDescription("Storage"));
            dataView.Filter = FilterSymbolEntries;

            UpdateProjectList();
        }

        private bool FilterSymbolEntries(Object currentEntry)
        {
            ItemSize currentItem = (currentEntry as ItemSize);
            bool isMatch = false;

            try
            {
                if (DataSizeViewerPackage.Options.UseRegExFiltering)
                    isMatch = (new Regex(mFilterString)).IsMatch(currentItem.Name);
                else
                    isMatch = currentItem.Name.Contains(mFilterString);
            }
            catch { }

            return isMatch &&
                    ((mShowDataSegment && currentItem.Storage.Contains("Data")) ||
                    (mShowTextSegment && currentItem.Storage.Contains("Text")));
        }

        private void ReloadProjectSymbols()
        {
            mSymbolParser.ClearSymbols();

            if (projectList.SelectedItem == null)
                return;

            String projectName = (String)projectList.SelectedItem.ToString();
            if (String.IsNullOrEmpty(projectName))
                return;

            Project project = null;
            try
            {
                project = myDTE.Solution.Item(projectName);
            }
            catch { }

            if (project == null)
                return;

            IProjectHandle projectNode = project.Object as IProjectHandle;
            if (projectNode == null)
                return;

            if (mToolchainService == null)
                return;

            string elfPath = null;
            string toolchainNMPath = null;

            string previousDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(Path.GetDirectoryName(project.FullName));
                elfPath = Path.GetFullPath(Path.Combine(projectNode.GetProperty("OutputDirectory"), projectNode.GetProperty("OutputFilename") + ".elf"));
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
            }

            ICCompilerToolchain toolchainC = mToolchainService.GetCCompilerToolchain(projectNode.ToolchainName, projectNode.GetProperty("ToolchainFlavour"));
            if (toolchainC != null)
                toolchainNMPath = Path.Combine(Path.GetDirectoryName(toolchainC.Compiler.FullPath), Path.GetFileName(toolchainC.Compiler.FullPath.Replace("gcc", "nm")));

            ICppCompilerToolchain toolchainCpp = mToolchainService.GetCppCompilerToolchain(projectNode.ToolchainName, projectNode.GetProperty("ToolchainFlavour"));
            if (toolchainCpp != null)
                toolchainNMPath = Path.Combine(Path.GetDirectoryName(toolchainCpp.CppCompiler.FullPath), Path.GetFileName(toolchainCpp.CppCompiler.FullPath.Replace("g++", "nm")));

            if (File.Exists(elfPath) && File.Exists(toolchainNMPath))
            {
                var dispatcher = Dispatcher.BeginInvoke(new Action(() => mSymbolParser.ReloadSymbols(elfPath, toolchainNMPath, DataSizeViewerPackage.Options.VerifyLocations)));

                dispatcher.Completed += (s, e) => { ShowSymbolTable(); };

                if (dispatcher.Status == System.Windows.Threading.DispatcherOperationStatus.Completed)
                    ShowSymbolTable();
            }
            else
            {
                ShowError("Could not find ELF file. Make sure that the default ELF file is available in the output directory.");
            }
        }

        private void ShowError(String message)
        {
            ShowError();
            errorMessage.Content = message;
        }
        private void ShowError()
        {
            errorMessage.Visibility = Visibility.Visible;
            symbolSize.Visibility = Visibility.Hidden;
        }

        private void ShowSymbolTable()
        {
            errorMessage.Content = string.Empty;
            symbolSize.Visibility = Visibility.Visible;
            errorMessage.Visibility = Visibility.Hidden;
        }

        private void UpdateProjectList()
        {
            string currentSelection = string.Empty;

            if (projectList.SelectedItem != null)
                currentSelection = projectList.SelectedItem.ToString();

            projectList.Items.Clear();
            foreach (Project p in myDTE.Solution.Projects)
            {
                if (File.Exists(p.FullName))
                    projectList.Items.Add(p.UniqueName);
            }

            if (string.IsNullOrEmpty(currentSelection))
                currentSelection = GetStartupProjectName(myDTE);

            try
            {
                projectList.SelectedItem = myDTE.Solution.Projects.Item(currentSelection).UniqueName;
            }
            catch { }

            ReloadProjectSymbols();
        }

        private static string GetStartupProjectName(DTE myDTE)
        {
            string startupProjectName = string.Empty;
            SolutionBuild solutionBuild = myDTE.Solution.SolutionBuild;

            if ((solutionBuild == null) || (solutionBuild.StartupProjects == null))
                return startupProjectName;

            foreach (string el in (Array)solutionBuild.StartupProjects)
                startupProjectName += el;

            return startupProjectName;
        }

        private void symbolSize_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (symbolSize.SelectedItem == null)
                return;

            string symbolLocation = (symbolSize.SelectedItem as ItemSize).Location;

            if ((symbolLocation == null) || (symbolLocation.Contains(':') == false))
                return;

            string fileName = symbolLocation.Substring(0, symbolLocation.LastIndexOf(':'));

            int fileLine = -1;
            bool fileLineValid = int.TryParse(symbolLocation.Substring(symbolLocation.LastIndexOf(':') + 1), out fileLine);

            if (File.Exists(fileName) && fileLineValid)
            {
                EnvDTE.Window w = myDTE.ItemOperations.OpenFile(Path.GetFullPath(fileName));

                if (w != null)
                    (w.Selection as EnvDTE.TextSelection).GotoLine(fileLine, true);
            }
            else
            {
                myDTE.StatusBar.Text = String.Format("Could not open file {0}", symbolLocation);
            }
        }

        void symbolSize_CopyCmdExecuted(object target, ExecutedRoutedEventArgs e)
        {
            StringBuilder copyContent = new StringBuilder();

            foreach (ItemSize i in symbolSize.SelectedItems)
              copyContent.AppendLine(String.Format("{0}, {1}, {2}", i.Size, i.Storage, i.Name));

            Clipboard.SetText(copyContent.ToString());
        }

        void symbolSize_CopyCmdCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = (symbolSize.Items.IsEmpty == false);
        }

        private void RefreshSymbolTable_Click(object sender, RoutedEventArgs e)
        {
            UpdateProjectList();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            DataSizeViewerPackage.Package.ShowOptionPage(typeof(OptionsPage));
        }    
    }
}
