using ImageSearch.Data;
using ImageSearch.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace ImageSearch
{
    public partial class MainWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly OcrService _ocrService;
        private readonly ImageScannerService _scannerService;
        private readonly SearchService _searchService;
        private string _currentSearchQuery = string.Empty;

        public MainWindow()
        {
            InitializeComponent();

            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageSearch");
            _dbService = new DatabaseService(appDataPath);
            _ocrService = new OcrService();
            _ocrService.Initialize();
            _scannerService = new ImageScannerService(_dbService, _ocrService);
            _searchService = new SearchService(_dbService);

            _scannerService.ProgressChanged += ScannerService_ProgressChanged;
            _scannerService.ScanCompleted += ScannerService_ScanCompleted;

            UpdateIndexedCount();
            
            LoadWindowIcon();
        }
        
        private void LoadWindowIcon()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string iconPath = Path.Combine(exeDir, "Htkis.ico");
                
                if (File.Exists(iconPath))
                {
                    var icon = new BitmapImage(new Uri(iconPath));
                    this.Icon = icon;
                }
            }
            catch
            {
                // 忽略图标加载错误，不影响程序运行
            }
        }

        private void UpdateIndexedCount()
        {
            Dispatcher.Invoke(() =>
            {
                IndexedCount.Text = _dbService.GetIndexedCount().ToString();
            });
        }

        private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                PerformSearch();
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            PerformSearch();
        }

        private void PerformSearch()
        {
            var query = SearchBox.Text.Trim();
            
            if (string.IsNullOrWhiteSpace(query))
            {
                MessageBox.Show("请输入搜索内容", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _currentSearchQuery = query;
            
            try
            {
                var result = _searchService.Search(query);
                
                DisplayResults(result.FilePaths);
                SearchTime.Text = $"{result.SearchTimeMs}ms";
                ResultCount.Text = result.FilePaths.Count.ToString();
                StatusText.Text = "搜索完成";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"搜索出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DisplayResults(List<string> filePaths)
        {
            ResultPanel.Children.Clear();

            if (filePaths.Count == 0)
            {
                var noResultText = new TextBlock
                {
                    Text = "未找到匹配的图片",
                    FontSize = 16,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(20)
                };
                ResultPanel.Children.Add(noResultText);
                return;
            }

            foreach (var filePath in filePaths)
            {
                try
                {
                    var imageCard = CreateImageCard(filePath);
                    ResultPanel.Children.Add(imageCard);
                }
                catch (Exception)
                {
                }
            }
        }

        private Border CreateImageCard(string filePath)
        {
            var border = new Border
            {
                Width = 180,
                Height = 180,
                Margin = new Thickness(10),
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var stackPanel = new StackPanel();

            var image = new Image
            {
                Width = 170,
                Height = 140,
                Stretch = System.Windows.Media.Stretch.UniformToFill,
                Margin = new Thickness(5, 5, 5, 0)
            };

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 170;
                    bitmap.DecodePixelHeight = 140;
                    bitmap.StreamSource = fs;
                    bitmap.EndInit();
                    image.Source = bitmap;
                }
            }
            catch (Exception)
            {
                image.Source = null;
            }

            var fileName = new TextBlock
            {
                Text = Path.GetFileName(filePath),
                FontSize = 11,
                Margin = new Thickness(5, 5, 5, 5),
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DarkGray,
                MaxHeight = 30
            };

            stackPanel.Children.Add(image);
            stackPanel.Children.Add(fileName);
            border.Child = stackPanel;

            border.MouseDown += (sender, e) =>
            {
                if (e.ClickCount == 2)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = filePath,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception)
                    {
                    }
                }
            };

            return border;
        }

        private void IndexButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                StartIndexing(dialog.FolderName);
            }
        }

        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true)
            {
                StartIndexing(dialog.FolderName);
            }
        }

        private void StartIndexing(string folderPath)
        {
            ProgressBorder.Visibility = Visibility.Visible;
            ResultScroll.Visibility = Visibility.Collapsed;
            StatusText.Text = "正在建立索引...";

            Task.Run(() => _scannerService.ScanAsync(folderPath));
        }

        private void ScannerService_ProgressChanged(object? sender, ScanProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                IndexProgress.Value = e.Progress;
                ProgressText.Text = e.Message;
                StatusText.Text = e.Message;
            });
        }

        private void ScannerService_ScanCompleted(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBorder.Visibility = Visibility.Collapsed;
                ResultScroll.Visibility = Visibility.Visible;
                UpdateIndexedCount();
                StatusText.Text = "索引建立完成";
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _ocrService.Dispose();
            base.OnClosed(e);
        }
    }
}