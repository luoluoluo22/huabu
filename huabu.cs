using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Linq;
using System.Net.Http.Headers;
using Microsoft.Win32;
using Quicker.Public;

// Chat message model
public class ChatMessage : INotifyPropertyChanged
{
    private string _content;
    private string _imageUrl;
    public string Role { get; set; }
    public string Content { 
        get => _content; 
        set { _content = value; OnPropertyChanged(nameof(Content)); } 
    }
    public string ImageUrl { 
        get => _imageUrl; 
        set { _imageUrl = value; OnPropertyChanged(nameof(ImageUrl)); } 
    }
    public SolidColorBrush BgColor { get; set; }
    public HorizontalAlignment Alignment { get; set; }
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class JaazCoreCanvas
{
    private class ToolbarIconInfo
    {
        public Border Border { get; set; }
        public System.Windows.Shapes.Path Path { get; set; }
        public InkCanvasEditingMode Mode { get; set; }
    }

    private static ObservableCollection<ChatMessage> _chatHistory = new ObservableCollection<ChatMessage>();
    private static InkCanvas _mainCanvas;
    private static Grid _canvasHost;
    private static TextBox _inputBox;
    private static ScrollViewer _chatScroll;
    private static TextBlock _txtZoom;
    private static Border _selectionPopbar;
    private static TextBox _popbarInput;
    private static Canvas _canvasOverlay;
    private static ScaleTransform _canvasScale = new ScaleTransform(1, 1);
    private static ScaleTransform _overlayScale = new ScaleTransform(1, 1);
    private static TranslateTransform _canvasPan = new TranslateTransform(0, 0);
    private static TranslateTransform _overlayPan = new TranslateTransform(0, 0);
    private static readonly HttpClient _httpClient = new HttpClient();
    private static bool _isMarqueeSelecting = false;
    private static bool _isCanvasPanning = false;
    private static bool _isImageDragging = false;
    private static bool _isImageResizing = false;
    private static Point _marqueeStart;
    private static Point _lastPanPoint;
    private static Point _imageDragStartCanvasPoint;
    private static Image _activeImage;
    private static double _imageStartLeft;
    private static double _imageStartTop;
    private static double _imageStartWidth;
    private static double _imageStartHeight;
    private static Border _marqueeRect;
    private static InkCanvasEditingMode _currentMode = InkCanvasEditingMode.Select;
    private static readonly List<ToolbarIconInfo> _modeTools = new List<ToolbarIconInfo>();
    private static ToolbarIconInfo _activeModeTool;
    private static ResizeHandle _activeResizeHandle = ResizeHandle.None;
    private const double ResizeHandleHitRadius = 14;
    private const double MinImageWidth = 60;

    private enum ResizeHandle
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private static string _apiUrl = "http://127.0.0.1:55557/v1/chat/completions";
    private static string _modelName = "gemini-3-flash";
    private static string _selectionSystemPrompt =
        "You are an image editing agent for canvas workflows. " +
        "When user provides an annotated reference image, you must generate a NEW edited result image based on the instruction and visual cues. " +
        "Do not keep the input unchanged. Do not only describe. Output a generated image result.";
    private static double _zoomLevel = 1.0;
    private static double _lastImageRight = 50;

    private static readonly string LogDir = @"F:\Desktop\kaifa\huabu";
    private static readonly string LogFile = Path.Combine(LogDir, "client_debug.log");

    public static void Exec(IStepContext context)
    {
        if (Application.Current == null)
        {
            var app = new Application();
            app.Dispatcher.Invoke(() => ShowMainWindow());
            app.Run();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(() => ShowMainWindow());
        }
    }

    private static void Log(string msg) {
        try {
            if (!Directory.Exists(LogDir)) return;
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        } catch {}
    }

    public static void ShowMainWindow()
    {
        // Static collection persists across window instances; reset per launch to avoid duplicated welcome messages.
        _chatHistory.Clear();
        Log("ShowMainWindow start");

        var window = new Window
        {
            Title = "Jaaz AI Assistant",
            Width = 1400, Height = 900,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 23))
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(45) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // --- Top Bar ---
        var topBarBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)), BorderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 45)), BorderThickness = new Thickness(0, 0, 0, 1) };
        var topBarGrid = new Grid();
        topBarBorder.Child = topBarGrid;
        var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 0, 0) };
        leftStack.Children.Add(new TextBlock { Text = "Jaaz Studio", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold });
        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 15, 0) };
        _txtZoom = new TextBlock { Text = "100%", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        var btnSettings = new Button { Content = "Settings", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.Gray, FontSize = 12, Cursor = Cursors.Hand };
        btnSettings.Click += (s, e) => ShowSettingsWindow();
        rightStack.Children.Add(_txtZoom); rightStack.Children.Add(btnSettings);
        topBarGrid.Children.Add(leftStack); topBarGrid.Children.Add(rightStack);
        Grid.SetRow(topBarBorder, 0);

        // --- Main Content ---
        var mainGrid = new Grid();
        Grid.SetRow(mainGrid, 1);
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });

        // Canvas container
        var canvasContainer = new Grid {
            Background = new SolidColorBrush(Color.FromRgb(15, 15, 15)),
            ClipToBounds = true
        };
        _canvasHost = canvasContainer;
        _mainCanvas = new InkCanvas { 
            Background = Brushes.Transparent, 
            AllowDrop = true,
            EditingMode = InkCanvasEditingMode.Select
        };
        var canvasTransforms = new TransformGroup();
        canvasTransforms.Children.Add(_canvasScale);
        canvasTransforms.Children.Add(_canvasPan);
        _mainCanvas.RenderTransform = canvasTransforms;
        _mainCanvas.DefaultDrawingAttributes.Color = Colors.Cyan;
        _mainCanvas.SelectionChanged += OnCanvasSelectionChanged;

        // Keep overlay interactive for its children (popbar), but let empty space pass mouse
        // events through to InkCanvas so drag-selection works.
        _canvasOverlay = new Canvas { IsHitTestVisible = true, Background = null };
        var overlayTransforms = new TransformGroup();
        overlayTransforms.Children.Add(_overlayScale);
        overlayTransforms.Children.Add(_overlayPan);
        _canvasOverlay.RenderTransform = overlayTransforms;
        
        canvasContainer.PreviewMouseWheel += (s, e) => {
            if (Keyboard.Modifiers == ModifierKeys.Control) {
                double delta = e.Delta > 0 ? 0.1 : -0.1;
                _zoomLevel = Math.Max(0.1, Math.Min(5, _zoomLevel + delta));
                _canvasScale.ScaleX = _canvasScale.ScaleY = _zoomLevel;
                _overlayScale.ScaleX = _overlayScale.ScaleY = _zoomLevel;
                _txtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
                HidePopbar();
                e.Handled = true;
            }
        };

        window.KeyDown += (s, e) => {
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) {
                if (_mainCanvas.Strokes.Count > 0) _mainCanvas.Strokes.RemoveAt(_mainCanvas.Strokes.Count - 1);
                else if (_mainCanvas.Children.Count > 0) {
                    _mainCanvas.Children.RemoveAt(_mainCanvas.Children.Count - 1);
                    UpdateLastImagePosition();
                }
            }
        };

        _selectionPopbar = CreateSelectionPopbar();
        _canvasOverlay.Children.Add(_selectionPopbar);
        _marqueeRect = CreateMarqueeRect();
        _canvasOverlay.Children.Add(_marqueeRect);

        _mainCanvas.PreviewMouseLeftButtonDown += OnCanvasMouseLeftButtonDown;
        _mainCanvas.PreviewMouseMove += OnCanvasMouseMove;
        _mainCanvas.PreviewMouseLeftButtonUp += OnCanvasMouseLeftButtonUp;
        _canvasOverlay.PreviewMouseLeftButtonDown += OnCanvasMouseLeftButtonDown;
        _canvasOverlay.PreviewMouseMove += OnCanvasMouseMove;
        _canvasOverlay.PreviewMouseLeftButtonUp += OnCanvasMouseLeftButtonUp;

        var floatingToolbar = new Border {
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)), CornerRadius = new CornerRadius(20),
            Padding = new Thickness(15, 5, 15, 5), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 30),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 15, Opacity = 0.5, ShadowDepth = 0 }
        };
        
        var pathPan = "M20,11V14A8,8 0 0,1 12,22A8,8 0 0,1 4,14V5A2,2 0 0,1 6,3A2,2 0 0,1 8,5V10.5H9V4A2,2 0 0,1 11,2A2,2 0 0,1 13,4V10.5H14V5A2,2 0 0,1 16,3A2,2 0 0,1 18,5V10.5H19V7A2,2 0 0,1 21,5A2,2 0 0,1 23,7V11H20Z";
        var pathSelect = "M7 2l12 11.2h-5.8l3.3 7.3-2.2.9-3.2-7.4-4.4 4.7z";
        var pathPen = "M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04c.39-.39.39-1.02 0-1.41l-2.34-2.34c-.39-.39-1.02-.39-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z";
        var pathImage = "M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z";
        var pathEraser = "M15.1,3.1 c-0.4,0-0.8,0.2-1.1,0.5L2.6,15.1c-0.6,0.6-0.6,1.6,0,2.2l2.8,2.8c0.3,0.3,0.7,0.5,1.1,0.5s0.8-0.2,1.1-0.5l4-4h10v-2h-8.2 l5.4-5.4c0.6-0.6,0.6-1.6,0-2.2l-2.8-2.8C15.9,3.3,15.5,3.1,15.1,3.1z M15.1,5.2l2.1,2.1L12.1,12.4L10,10.3L15.1,5.2z";
        var pathTrash = "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12M19 4h-3.5l-1-1h-5l-1 1H5v2h14V4";

        var toolStack = new StackPanel { Orientation = Orientation.Horizontal };
        _modeTools.Clear();

        var panTool = CreateModeToolbarIcon(pathPan, "Hand", InkCanvasEditingMode.None);
        var selectTool = CreateModeToolbarIcon(pathSelect, "Select", InkCanvasEditingMode.Select);
        var penTool = CreateModeToolbarIcon(pathPen, "Annotate", InkCanvasEditingMode.Ink);
        var imageTool = CreateActionToolbarIcon(pathImage, "Image", () => UploadImage());
        var eraserTool = CreateModeToolbarIcon(pathEraser, "Eraser", InkCanvasEditingMode.EraseByStroke);
        var clearTool = CreateActionToolbarIcon(pathTrash, "Clear", () => { _mainCanvas.Strokes.Clear(); _mainCanvas.Children.Clear(); _lastImageRight = 50; HidePopbar(); });

        toolStack.Children.Add(panTool.Border);
        toolStack.Children.Add(selectTool.Border);
        toolStack.Children.Add(penTool.Border);
        toolStack.Children.Add(imageTool);
        toolStack.Children.Add(eraserTool.Border);
        toolStack.Children.Add(new Separator { Width = 1, Background = Brushes.DimGray, Margin = new Thickness(10, 5, 10, 5) });
        toolStack.Children.Add(clearTool);

        floatingToolbar.Child = toolStack;
        ActivateModeTool(selectTool);

        canvasContainer.Children.Add(_mainCanvas);
        canvasContainer.Children.Add(_canvasOverlay);
        canvasContainer.Children.Add(floatingToolbar);

        var sidebarGrid = new Grid { Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)) };
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chatScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var itemsControl = new ItemsControl { ItemsSource = _chatHistory };
        itemsControl.ItemTemplate = CreateMessageTemplate();
        _chatScroll.Content = itemsControl;
        var inputArea = CreateChatInputArea();
        Grid.SetRow(_chatScroll, 0); Grid.SetRow(inputArea, 1);
        sidebarGrid.Children.Add(_chatScroll); sidebarGrid.Children.Add(inputArea);

        Grid.SetColumn(canvasContainer, 0); Grid.SetColumn(sidebarGrid, 1);
        mainGrid.Children.Add(canvasContainer); mainGrid.Children.Add(sidebarGrid);
        rootGrid.Children.Add(topBarBorder); rootGrid.Children.Add(mainGrid);

        window.Content = rootGrid;
        window.Show();
        AddMessage("Assistant", "System ready. Select elements and generate. Ctrl + mouse wheel to zoom.");
    }

    private static Border CreateSelectionPopbar() {
        var border = new Border {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Width = 320,
            Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, Opacity = 0.3 }
        };
        var stack = new StackPanel();
        
        // WPF TextBox does not support PlaceholderText directly.
        var grid = new Grid();
        _popbarInput = new TextBox { 
            Height = 35, 
            Background = new SolidColorBrush(Color.FromRgb(30,30,35)), 
            Foreground = Brushes.White, BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8,0,8,0)
        };
        var placeholder = new TextBlock { 
            Text = "Enter instruction...", 
            Foreground = Brushes.Gray, 
            IsHitTestVisible = false, 
            VerticalAlignment = VerticalAlignment.Center, 
            Margin = new Thickness(10,0,0,0) 
        };
        _popbarInput.TextChanged += (s, e) => placeholder.Visibility = string.IsNullOrEmpty(_popbarInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        
        grid.Children.Add(_popbarInput);
        grid.Children.Add(placeholder);

        _popbarInput.KeyDown += (s, e) => { if (e.Key == Key.Enter) HandleSelectedContentRequest(); };
        
        var btnAction = new Button { 
            Content = "Generate", 
            Height = 30, Margin = new Thickness(0,5,0,0),
            Background = new SolidColorBrush(Color.FromRgb(60, 120, 240)), 
            Foreground = Brushes.White, BorderThickness = new Thickness(0) 
        };
        btnAction.Click += (s, e) => HandleSelectedContentRequest();
        
        stack.Children.Add(grid);
        stack.Children.Add(btnAction);
        border.Child = stack;
        return border;
    }

    private static void OnCanvasSelectionChanged(object sender, EventArgs e) {
        var selectedElements = _mainCanvas.GetSelectedElements();
        var selectedStrokes = _mainCanvas.GetSelectedStrokes();

        if (selectedElements.Count > 0 || selectedStrokes.Count > 0) {
            Rect bounds = Rect.Empty;
            foreach (var el in selectedElements) {
                var r = new Rect(InkCanvas.GetLeft(el), InkCanvas.GetTop(el), el.RenderSize.Width, el.RenderSize.Height);
                bounds.Union(r);
            }
            foreach (var stroke in selectedStrokes) bounds.Union(stroke.GetBounds());

            Point canvasPos = _mainCanvas.TranslatePoint(new Point(bounds.Left, bounds.Bottom), _canvasOverlay);
            Canvas.SetLeft(_selectionPopbar, Math.Max(10, canvasPos.X));
            Canvas.SetTop(_selectionPopbar, Math.Max(10, canvasPos.Y + 10));
            _selectionPopbar.Visibility = Visibility.Visible;
        } else {
            HidePopbar();
        }
    }

    private static void HidePopbar() {
        _selectionPopbar.Visibility = Visibility.Collapsed;
        _popbarInput.Text = "";
    }

    private static Border CreateMarqueeRect() {
        return new Border {
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 160, 255)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(40, 80, 160, 255)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
    }

    private static void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (_currentMode == InkCanvasEditingMode.None) {
            _isCanvasPanning = true;
            IInputElement panSurface = (IInputElement)_canvasHost ?? _mainCanvas;
            _lastPanPoint = e.GetPosition(panSurface);
            _mainCanvas.CaptureMouse();
            _canvasOverlay?.CaptureMouse();
            _mainCanvas.Cursor = Cursors.Hand;
            if (_canvasOverlay != null) _canvasOverlay.Cursor = Cursors.Hand;
            Log($"Pan start: p=({_lastPanPoint.X:F1},{_lastPanPoint.Y:F1}), pan=({_canvasPan.X:F1},{_canvasPan.Y:F1})");
            e.Handled = true;
            return;
        }

        if (_currentMode != InkCanvasEditingMode.Select) return;

        Point overlayPoint = e.GetPosition(_canvasOverlay);
        Point canvasPoint = _canvasOverlay.TranslatePoint(overlayPoint, _mainCanvas);

        var hitImage = FindTopImageAtPoint(canvasPoint);
        if (hitImage != null) {
            _activeImage = hitImage;
            _mainCanvas.Select(new System.Windows.Ink.StrokeCollection(), new List<UIElement> { hitImage });
            _activeResizeHandle = HitTestResizeHandle(hitImage, canvasPoint);

            _imageDragStartCanvasPoint = canvasPoint;
            _imageStartLeft = GetElementLeft(hitImage);
            _imageStartTop = GetElementTop(hitImage);
            _imageStartWidth = GetElementWidth(hitImage);
            _imageStartHeight = GetElementHeight(hitImage);

            if (_activeResizeHandle != ResizeHandle.None) {
                _isImageResizing = true;
                _mainCanvas.Cursor = GetCursorForResizeHandle(_activeResizeHandle);
                if (_canvasOverlay != null) _canvasOverlay.Cursor = _mainCanvas.Cursor;
                Log($"Image resize start: handle={_activeResizeHandle}, left={_imageStartLeft:F1}, top={_imageStartTop:F1}, width={_imageStartWidth:F1}");
            } else {
                _isImageDragging = true;
                _mainCanvas.Cursor = Cursors.SizeAll;
                if (_canvasOverlay != null) _canvasOverlay.Cursor = Cursors.SizeAll;
                Log($"Image drag start: left={_imageStartLeft:F1}, top={_imageStartTop:F1}");
            }

            _mainCanvas.CaptureMouse();
            _canvasOverlay?.CaptureMouse();
            e.Handled = true;
            return;
        }

        bool hasSelection = _mainCanvas.GetSelectedElements().Count > 0 || _mainCanvas.GetSelectedStrokes().Count > 0;
        if (IsPointerOnSelectableContent(canvasPoint)) return;
        if (hasSelection) _mainCanvas.Select(new System.Windows.Ink.StrokeCollection(), new List<UIElement>());

        _isMarqueeSelecting = true;
        _marqueeStart = overlayPoint;

        Canvas.SetLeft(_marqueeRect, _marqueeStart.X);
        Canvas.SetTop(_marqueeRect, _marqueeStart.Y);
        _marqueeRect.Width = 0;
        _marqueeRect.Height = 0;
        _marqueeRect.Visibility = Visibility.Visible;
        HidePopbar();

        _mainCanvas.CaptureMouse();
        e.Handled = true;
    }

    private static bool IsPointerOnSelectableContent(Point canvasPoint) {
        foreach (UIElement el in _mainCanvas.Children) {
            if (el is FrameworkElement fe) {
                double x = InkCanvas.GetLeft(fe);
                double y = InkCanvas.GetTop(fe);
                if (double.IsNaN(x)) x = 0;
                if (double.IsNaN(y)) y = 0;
                double w = fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width;
                double h = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;
                if (w > 0 && h > 0 && new Rect(x, y, w, h).Contains(canvasPoint)) return true;
            }
        }
        foreach (var stroke in _mainCanvas.Strokes) {
            if (stroke.HitTest(canvasPoint, 3)) return true;
        }
        return false;
    }

    private static void OnCanvasMouseMove(object sender, MouseEventArgs e) {
        if (_isCanvasPanning) {
            IInputElement panSurface = (IInputElement)_canvasHost ?? _mainCanvas;
            Point panPoint = e.GetPosition(panSurface);
            Vector delta = panPoint - _lastPanPoint;
            _canvasPan.X += delta.X;
            _canvasPan.Y += delta.Y;
            _overlayPan.X += delta.X;
            _overlayPan.Y += delta.Y;
            _lastPanPoint = panPoint;
            e.Handled = true;
            return;
        }

        // Cursor feedback for image interactions in Select mode.
        if (_currentMode == InkCanvasEditingMode.Select && !_isImageDragging && !_isImageResizing) {
            Point hoverOverlayPoint = e.GetPosition(_canvasOverlay);
            Point hoverCanvasPoint = _canvasOverlay.TranslatePoint(hoverOverlayPoint, _mainCanvas);
            var hoverImage = FindTopImageAtPoint(hoverCanvasPoint);
            if (hoverImage != null) {
                var handle = HitTestResizeHandle(hoverImage, hoverCanvasPoint);
                if (handle != ResizeHandle.None) {
                    var cursor = GetCursorForResizeHandle(handle);
                    _mainCanvas.Cursor = cursor;
                    if (_canvasOverlay != null) _canvasOverlay.Cursor = cursor;
                } else {
                    _mainCanvas.Cursor = Cursors.SizeAll;
                    if (_canvasOverlay != null) _canvasOverlay.Cursor = Cursors.SizeAll;
                }
            } else {
                _mainCanvas.Cursor = Cursors.Arrow;
                if (_canvasOverlay != null) _canvasOverlay.Cursor = Cursors.Arrow;
            }
        }

        if (_isImageDragging && _activeImage != null) {
            Point canvasPoint = _canvasOverlay.TranslatePoint(e.GetPosition(_canvasOverlay), _mainCanvas);
            Vector delta = canvasPoint - _imageDragStartCanvasPoint;
            InkCanvas.SetLeft(_activeImage, _imageStartLeft + delta.X);
            InkCanvas.SetTop(_activeImage, _imageStartTop + delta.Y);
            e.Handled = true;
            return;
        }

        if (_isImageResizing && _activeImage != null) {
            Point canvasPoint = _canvasOverlay.TranslatePoint(e.GetPosition(_canvasOverlay), _mainCanvas);
            ResizeActiveImage(canvasPoint);
            e.Handled = true;
            return;
        }

        if (!_isMarqueeSelecting) return;

        Point currentPoint = e.GetPosition(_canvasOverlay);
        double left = Math.Min(_marqueeStart.X, currentPoint.X);
        double top = Math.Min(_marqueeStart.Y, currentPoint.Y);
        double width = Math.Abs(currentPoint.X - _marqueeStart.X);
        double height = Math.Abs(currentPoint.Y - _marqueeStart.Y);

        Canvas.SetLeft(_marqueeRect, left);
        Canvas.SetTop(_marqueeRect, top);
        _marqueeRect.Width = width;
        _marqueeRect.Height = height;
    }

    private static void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (_isImageDragging || _isImageResizing) {
            _isImageDragging = false;
            _isImageResizing = false;
            _activeResizeHandle = ResizeHandle.None;
            _mainCanvas.ReleaseMouseCapture();
            _canvasOverlay?.ReleaseMouseCapture();
            _mainCanvas.Cursor = Cursors.Arrow;
            if (_canvasOverlay != null) _canvasOverlay.Cursor = Cursors.Arrow;
            Log("Image transform end");
            e.Handled = true;
            return;
        }

        if (_isCanvasPanning) {
            _isCanvasPanning = false;
            _mainCanvas.ReleaseMouseCapture();
            _canvasOverlay?.ReleaseMouseCapture();
            var targetCursor = _currentMode == InkCanvasEditingMode.None ? Cursors.Hand : Cursors.Arrow;
            _mainCanvas.Cursor = targetCursor;
            if (_canvasOverlay != null) _canvasOverlay.Cursor = targetCursor;
            Log($"Pan end: pan=({_canvasPan.X:F1},{_canvasPan.Y:F1})");
            e.Handled = true;
            return;
        }

        if (!_isMarqueeSelecting) return;

        _isMarqueeSelecting = false;
        _mainCanvas.ReleaseMouseCapture();

        var left = Canvas.GetLeft(_marqueeRect);
        var top = Canvas.GetTop(_marqueeRect);
        var width = _marqueeRect.Width;
        var height = _marqueeRect.Height;
        _marqueeRect.Visibility = Visibility.Collapsed;

        if (width < 3 || height < 3) return;

        // Overlay rect is in screen/overlay coordinates; convert to canvas logical coordinates
        // so selection works correctly at any zoom level.
        Point canvasTopLeft = _canvasOverlay.TranslatePoint(new Point(left, top), _mainCanvas);
        Point canvasBottomRight = _canvasOverlay.TranslatePoint(new Point(left + width, top + height), _mainCanvas);
        Rect selectionRect = new Rect(canvasTopLeft, canvasBottomRight);
        var hitStrokes = _mainCanvas.Strokes.HitTest(selectionRect, 2);
        var hitElements = _mainCanvas.Children
            .OfType<UIElement>()
            .Where(el => {
                double x = InkCanvas.GetLeft(el);
                double y = InkCanvas.GetTop(el);
                if (double.IsNaN(x)) x = 0;
                if (double.IsNaN(y)) y = 0;
                double w = (el as FrameworkElement)?.ActualWidth ?? 0;
                double h = (el as FrameworkElement)?.ActualHeight ?? 0;
                if (w <= 0 || h <= 0) {
                    w = (el as FrameworkElement)?.Width ?? 0;
                    h = (el as FrameworkElement)?.Height ?? 0;
                }
                if (w <= 0 || h <= 0) return false;
                return selectionRect.IntersectsWith(new Rect(x, y, w, h));
            })
            .ToList();

        _mainCanvas.Select(hitStrokes, hitElements);
        e.Handled = true;
    }

    private static Image FindTopImageAtPoint(Point canvasPoint) {
        for (int i = _mainCanvas.Children.Count - 1; i >= 0; i--) {
            if (_mainCanvas.Children[i] is Image image) {
                double left = GetElementLeft(image);
                double top = GetElementTop(image);
                double width = GetElementWidth(image);
                double height = GetElementHeight(image);
                if (width <= 0 || height <= 0) continue;
                // Allow corner hit area to extend slightly outside the image bounds,
                // otherwise corner resizing can incorrectly fall through to marquee selection.
                var hitRect = new Rect(left, top, width, height);
                hitRect.Inflate(ResizeHandleHitRadius, ResizeHandleHitRadius);
                if (hitRect.Contains(canvasPoint)) return image;
            }
        }
        return null;
    }

    private static ResizeHandle HitTestResizeHandle(Image image, Point canvasPoint) {
        double left = GetElementLeft(image);
        double top = GetElementTop(image);
        double width = GetElementWidth(image);
        double height = GetElementHeight(image);
        if (width <= 0 || height <= 0) return ResizeHandle.None;

        var topLeft = new Point(left, top);
        var topRight = new Point(left + width, top);
        var bottomLeft = new Point(left, top + height);
        var bottomRight = new Point(left + width, top + height);

        if ((canvasPoint - topLeft).Length <= ResizeHandleHitRadius) return ResizeHandle.TopLeft;
        if ((canvasPoint - topRight).Length <= ResizeHandleHitRadius) return ResizeHandle.TopRight;
        if ((canvasPoint - bottomLeft).Length <= ResizeHandleHitRadius) return ResizeHandle.BottomLeft;
        if ((canvasPoint - bottomRight).Length <= ResizeHandleHitRadius) return ResizeHandle.BottomRight;

        return ResizeHandle.None;
    }

    private static Cursor GetCursorForResizeHandle(ResizeHandle handle) {
        return (handle == ResizeHandle.TopLeft || handle == ResizeHandle.BottomRight) ? Cursors.SizeNWSE : Cursors.SizeNESW;
    }

    private static double GetElementLeft(FrameworkElement element) {
        double x = InkCanvas.GetLeft(element);
        return double.IsNaN(x) ? 0 : x;
    }

    private static double GetElementTop(FrameworkElement element) {
        double y = InkCanvas.GetTop(element);
        return double.IsNaN(y) ? 0 : y;
    }

    private static double GetElementWidth(FrameworkElement element) {
        if (element.ActualWidth > 0) return element.ActualWidth;
        if (!double.IsNaN(element.Width) && element.Width > 0) return element.Width;
        return 0;
    }

    private static double GetElementHeight(FrameworkElement element) {
        if (element.ActualHeight > 0) return element.ActualHeight;
        if (!double.IsNaN(element.Height) && element.Height > 0) return element.Height;
        return 0;
    }

    private static void ResizeActiveImage(Point currentCanvasPoint) {
        if (_activeImage == null) return;
        if (_imageStartWidth <= 0) return;

        // Keep original aspect ratio while resizing by corners.
        double aspect = _imageStartHeight > 0 ? _imageStartWidth / _imageStartHeight : 1.0;
        double dx = currentCanvasPoint.X - _imageDragStartCanvasPoint.X;
        double signedDelta = (_activeResizeHandle == ResizeHandle.TopLeft || _activeResizeHandle == ResizeHandle.BottomLeft) ? -dx : dx;

        double targetWidth = Math.Max(MinImageWidth, _imageStartWidth + signedDelta);
        double targetHeight = Math.Max(1, targetWidth / Math.Max(0.0001, aspect));

        double newLeft = _imageStartLeft;
        if (_activeResizeHandle == ResizeHandle.TopLeft || _activeResizeHandle == ResizeHandle.BottomLeft) {
            double right = _imageStartLeft + _imageStartWidth;
            newLeft = right - targetWidth;
        }

        InkCanvas.SetLeft(_activeImage, newLeft);
        _activeImage.Width = targetWidth;
        if (!double.IsNaN(_activeImage.Height) && _activeImage.Height > 0) _activeImage.Height = targetHeight;
    }

    private static void HandleSelectedContentRequest() {
        string prompt = _popbarInput.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        HidePopbar();

        try {
            var rtb = RenderSelectionToBitmap();
            string base64Image = ConvertBitmapToBase64(rtb);
            string previewPath = SaveBitmapToTempPng(rtb);
            AddMessage("User", "[Selection] " + prompt, previewPath);
            string structuredPrompt = BuildSelectionPrompt(prompt);
            HandleAiRequest(structuredPrompt, base64Image, true);
        } catch (Exception ex) {
            Log("Render Error: " + ex.Message);
            AddMessage("User", "[Selection] " + prompt);
            HandleAiRequest(prompt, null);
        }
    }

    private static string BuildSelectionPrompt(string userPrompt) {
        return
            "<selection_edit_task>\n" +
            "Follow the user instruction to edit/regenerate image content according to the visual annotation.\n" +
            "The cyan strokes/arrows in the image are guidance for changes.\n" +
            "You MUST generate a new result image, not return unchanged input.\n" +
            "</selection_edit_task>\n\n" +
            "<input_images count=\"1\">\n" +
            "<image index=\"1\" file_id=\"selection_preview\" />\n" +
            "</input_images>\n\n" +
            "<user_instruction>\n" + userPrompt + "\n</user_instruction>";
    }

    private static RenderTargetBitmap RenderSelectionToBitmap() {
        var elements = _mainCanvas.GetSelectedElements();
        var strokes = _mainCanvas.GetSelectedStrokes();
        Rect bounds = Rect.Empty;
        foreach (var el in elements) bounds.Union(new Rect(InkCanvas.GetLeft(el), InkCanvas.GetTop(el), el.RenderSize.Width, el.RenderSize.Height));
        foreach (var s in strokes) bounds.Union(s.GetBounds());

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;

        var rtb = new RenderTargetBitmap((int)bounds.Width + 20, (int)bounds.Height + 20, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) {
            dc.PushTransform(new TranslateTransform(-bounds.Left + 10, -bounds.Top + 10));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15,15,15)), null, new Rect(bounds.Left-10, bounds.Top-10, bounds.Width+20, bounds.Height+20));
            foreach (var el in elements) {
                if (el is Image img) dc.DrawImage(img.Source, new Rect(InkCanvas.GetLeft(el), InkCanvas.GetTop(el), el.RenderSize.Width, el.RenderSize.Height));
            }
            // Match on-canvas visual stacking: draw ink after elements so annotations stay on top.
            foreach (var s in strokes) s.Draw(dc);
        }
        rtb.Render(dv);
        return rtb;
    }

    private static string ConvertBitmapToBase64(RenderTargetBitmap rtb) {
        if (rtb == null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var ms = new MemoryStream()) {
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
    }

    private static string SaveBitmapToTempPng(RenderTargetBitmap rtb) {
        if (rtb == null) return null;
        try {
            string file = Path.Combine(Path.GetTempPath(), $"jaaz_selection_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                encoder.Save(fs);
            }
            return file;
        } catch {
            return null;
        }
    }

    private static async void HandleAiRequest(string prompt = null, string base64Img = null, bool selectionMode = false) {
        string text = prompt ?? _inputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) && base64Img == null) return;
        
        if (prompt == null) { AddMessage("User", text); _inputBox.Clear(); }
        
        var assistantMsg = new ChatMessage { Role = "Assistant", Content = "", BgColor = new SolidColorBrush(Color.FromRgb(50, 50, 55)), Alignment = HorizontalAlignment.Left };
        _chatHistory.Add(assistantMsg); _chatScroll.ScrollToEnd();

        try {
            object body;
            object[] messages;
            if (selectionMode) {
                messages = new object[] {
                    new { role = "system", content = _selectionSystemPrompt },
                    new { role = "user", content = text ?? "" }
                };
            } else {
                messages = new object[] {
                    new { role = "user", content = text ?? "" }
                };
            }

            if (!string.IsNullOrEmpty(base64Img)) {
                string dataUrl = $"data:image/png;base64,{base64Img}";
                body = new {
                    model = _modelName,
                    stream = true,
                    messages = messages,
                    files = new[] {
                        new {
                            filename = $"selection_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                            mime_type = "image/png",
                            file_data = dataUrl
                        }
                    }
                };
            } else {
                body = new {
                    model = _modelName,
                    stream = true,
                    messages = messages
                };
            }

            var content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl) { Content = content };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)) {
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(stream)) {
                    StringBuilder sb = new StringBuilder();
                    string line;
                    while ((line = await reader.ReadLineAsync()) != null) {
                        if (line.StartsWith("data: ")) {
                            string d = line.Substring(6).Trim();
                            if (d == "[DONE]") break;
                            try {
                                dynamic chunk = Newtonsoft.Json.JsonConvert.DeserializeObject(d);
                                string c = chunk.choices[0].delta?.content;
                                if (!string.IsNullOrEmpty(c)) { sb.Append(c); assistantMsg.Content = sb.ToString(); }
                            } catch {}
                        }
                    }
                    ParseAndCleanImagesFromMessage(assistantMsg);
                }
            }
        } catch (Exception ex) { assistantMsg.Content = "Error: " + ex.Message; }
    }

    private static void UpdateLastImagePosition() {
        _lastImageRight = 50;
        foreach (UIElement child in _mainCanvas.Children) {
            if (child is Image img) _lastImageRight = Math.Max(_lastImageRight, InkCanvas.GetLeft(img) + 420);
        }
    }

    private static FrameworkElement CreateChatInputArea() {
        var stack = new StackPanel { Margin = new Thickness(10) };
        _inputBox = new TextBox { Height = 60, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Background = new SolidColorBrush(Color.FromRgb(35,35,40)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(5), CaretBrush = Brushes.Cyan };
        _inputBox.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { HandleAiRequest(); e.Handled = true; } };
        var btnSend = new Button { Content = "Send", Height = 30, Margin = new Thickness(0, 5, 0, 0), Background = new SolidColorBrush(Color.FromRgb(60, 120, 240)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        btnSend.Click += (s, e) => HandleAiRequest();
        stack.Children.Add(_inputBox); stack.Children.Add(btnSend);
        return stack;
    }

    private static void ParseAndCleanImagesFromMessage(ChatMessage msg) {
        string pattern = @"!\[.*?\]\((https?://.*?)\)";
        Match match = Regex.Match(msg.Content, pattern);
        if (match.Success) {
            string url = match.Groups[1].Value;
            Application.Current.Dispatcher.Invoke(() => {
                msg.ImageUrl = url; AddImageToCanvas(url);
                msg.Content = Regex.Replace(msg.Content, pattern, "").Trim();
                if (string.IsNullOrEmpty(msg.Content)) msg.Content = "Image generated and inserted into canvas.";
            });
        }
    }

    private static void AddImageToCanvas(string path) {
        try {
            var b = new BitmapImage(new Uri(path, UriKind.RelativeOrAbsolute));
            var img = new Image { Source = b, Width = 400, Stretch = Stretch.Uniform };
            InkCanvas.SetLeft(img, _lastImageRight); InkCanvas.SetTop(img, 100);
            _mainCanvas.Children.Add(img); _lastImageRight += 420;
            var selectTool = _modeTools.FirstOrDefault(t => t.Mode == InkCanvasEditingMode.Select);
            if (selectTool != null) ActivateModeTool(selectTool);
            else ApplyCanvasMode(InkCanvasEditingMode.Select);
        } catch {}
    }

    private static ToolbarIconInfo CreateModeToolbarIcon(string pathData, string tooltip, InkCanvasEditingMode mode) {
        var border = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tooltip, Margin = new Thickness(2,0,2,0) };
        var path = new System.Windows.Shapes.Path {
            Data = Geometry.Parse(pathData),
            Fill = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            Stretch = Stretch.Uniform,
            Width = 18, Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btn = new Button {
            Content = path, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FocusVisualStyle = null
        };
        btn.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
            <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
                <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </ControlTemplate>");

        var info = new ToolbarIconInfo { Border = border, Path = path, Mode = mode };
        btn.IsHitTestVisible = false;
        border.PreviewMouseLeftButtonDown += (s, e) => {
            Log($"Tool click(border): {tooltip}, requested={mode}, current={_currentMode}, active={_activeModeTool?.Mode}");
            // Clicking the active non-select tool toggles back to default Select mode.
            if (_activeModeTool == info && info.Mode != InkCanvasEditingMode.Select) {
                var selectTool = _modeTools.FirstOrDefault(t => t.Mode == InkCanvasEditingMode.Select);
                if (selectTool != null) ActivateModeTool(selectTool);
                e.Handled = true;
                return;
            }
            ActivateModeTool(info);
            e.Handled = true;
        };
        border.Child = btn;

        border.MouseEnter += (s, e) => {
            if (_activeModeTool == info) return;
            border.Background = new SolidColorBrush(Color.FromRgb(60, 60, 65));
            path.Fill = Brushes.White;
        };
        border.MouseLeave += (s, e) => {
            if (_activeModeTool == info) return;
            border.Background = Brushes.Transparent;
            path.Fill = new SolidColorBrush(Color.FromRgb(200, 200, 200));
        };

        _modeTools.Add(info);
        return info;
    }

    private static UIElement CreateActionToolbarIcon(string pathData, string tooltip, Action onClick) {
        var border = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tooltip, Margin = new Thickness(2,0,2,0) };
        var path = new System.Windows.Shapes.Path {
            Data = Geometry.Parse(pathData),
            Fill = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            Stretch = Stretch.Uniform,
            Width = 18, Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btn = new Button {
            Content = path, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FocusVisualStyle = null
        };
        btn.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
            <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
                <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </ControlTemplate>");
        btn.IsHitTestVisible = false;
        border.PreviewMouseLeftButtonDown += (s, e) => {
            Log($"Action tool click(border): {tooltip}, current={_currentMode}");
            onClick?.Invoke();
            e.Handled = true;
        };
        border.Child = btn;

        border.MouseEnter += (s, e) => { border.Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)); path.Fill = Brushes.White; };
        border.MouseLeave += (s, e) => { border.Background = Brushes.Transparent; path.Fill = new SolidColorBrush(Color.FromRgb(200, 200, 200)); };
        return border;
    }

    private static void ActivateModeTool(ToolbarIconInfo target) {
        if (target == null) return;
        Log($"ActivateModeTool: target={target.Mode}, previous={_currentMode}, modeTools={_modeTools.Count}");
        _activeModeTool = target;
        ApplyCanvasMode(target.Mode);

        foreach (var tool in _modeTools) {
            bool active = ReferenceEquals(tool, target);
            if (active) {
                tool.Border.Background = new SolidColorBrush(Color.FromRgb(34, 75, 160));
                tool.Path.Fill = new SolidColorBrush(Color.FromRgb(120, 185, 255));
            } else {
                tool.Border.Background = Brushes.Transparent;
                tool.Path.Fill = new SolidColorBrush(Color.FromRgb(200, 200, 200));
            }
        }
    }

    private static void ApplyCanvasMode(InkCanvasEditingMode mode) {
        var prev = _currentMode;
        _currentMode = mode;
        _mainCanvas.EditingMode = mode;
        _mainCanvas.EditingModeInverted = InkCanvasEditingMode.None;
        var targetCursor = mode == InkCanvasEditingMode.None ? Cursors.Hand : Cursors.Arrow;
        _mainCanvas.Cursor = targetCursor;
        if (_canvasOverlay != null) _canvasOverlay.Cursor = targetCursor;
        _mainCanvas.Focus();
        Log($"ApplyCanvasMode: {prev} -> {mode}, inkCanvasMode={_mainCanvas.EditingMode}, focused={_mainCanvas.IsKeyboardFocusWithin}");
    }

    private static DataTemplate CreateMessageTemplate() {
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(@"
        <DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
            <Border Background='{Binding BgColor}' CornerRadius='10' Padding='10' Margin='5' HorizontalAlignment='{Binding Alignment}' MaxWidth='350'>
                <StackPanel><TextBlock Text='{Binding Role}' FontWeight='Bold' Foreground='Gray' FontSize='10' Margin='0,0,0,3'/><TextBlock Text='{Binding Content}' Foreground='White' TextWrapping='Wrap' FontSize='13' /><Image Source='{Binding ImageUrl}' MaxWidth='320' Margin='0,8,0,0'><Image.Style><Style TargetType='Image'><Setter Property='Visibility' Value='Visible'/><Style.Triggers><DataTrigger Binding='{Binding ImageUrl}' Value='{x:Null}'><Setter Property='Visibility' Value='Collapsed'/></DataTrigger><DataTrigger Binding='{Binding ImageUrl}' Value=''><Setter Property='Visibility' Value='Collapsed'/></DataTrigger></Style.Triggers></Style></Image.Style></Image></StackPanel>
            </Border>
        </DataTemplate>");
    }

    private static void AddMessage(string r, string c, string imageUrl = null) {
        _chatHistory.Add(new ChatMessage {
            Role = r,
            Content = c,
            ImageUrl = imageUrl,
            BgColor = r == "User" ? new SolidColorBrush(Color.FromRgb(40, 50, 80)) : new SolidColorBrush(Color.FromRgb(50, 50, 55)),
            Alignment = r == "User" ? HorizontalAlignment.Right : HorizontalAlignment.Left
        });
        _chatScroll?.ScrollToEnd();
    }
    private static void UploadImage() { var d = new OpenFileDialog { Filter = "Image Files|*.png;*.jpg;*.jpeg" }; if (d.ShowDialog() == true) AddImageToCanvas(d.FileName); }
    private static bool IsImagePath(string p) { if (string.IsNullOrEmpty(p)) return false; string e = Path.GetExtension(p).ToLower(); return e == ".jpg" || e == ".jpeg" || e == ".png"; }
    private static void ShowSettingsWindow() {
        var w = new Window { Title = "Settings", Width = 400, Height = 250, Background = new SolidColorBrush(Color.FromRgb(30,30,35)), Foreground = Brushes.White, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var s = new StackPanel { Margin = new Thickness(20) };
        s.Children.Add(new TextBlock { Text = "API URL:" }); var u = new TextBox { Text = _apiUrl, Margin = new Thickness(0,0,0,10) };
        s.Children.Add(new TextBlock { Text = "Model:" }); var m = new TextBox { Text = _modelName, Margin = new Thickness(0,0,0,10) };
        var b = new Button { Content = "Save Settings", Height = 35, Background = new SolidColorBrush(Color.FromRgb(63, 81, 181)), Foreground = Brushes.White };
        b.Click += (x, y) => { _apiUrl = u.Text; _modelName = m.Text; w.Close(); };
        s.Children.Add(u); s.Children.Add(m); s.Children.Add(b); w.Content = s; w.ShowDialog();
    }
}
