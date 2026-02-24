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
    private static TextBox _inputBox;
    private static ScrollViewer _chatScroll;
    private static TextBlock _txtZoom;
    private static Border _selectionPopbar;
    private static TextBox _popbarInput;
    private static Canvas _canvasOverlay;
    private static ScaleTransform _canvasScale = new ScaleTransform(1, 1);
    private static TranslateTransform _canvasPan = new TranslateTransform(0, 0);
    private static TransformGroup _worldTransform = new TransformGroup();
    private static readonly HttpClient _httpClient = new HttpClient();
    private static bool _isMarqueeSelecting = false;
    private static Point _marqueeStart;
    private static Border _marqueeRect;
    private static bool _isPanningCanvas = false;
    private static Point _panStartOverlay;
    private static double _panStartX;
    private static double _panStartY;
    private static bool _isDraggingImage = false;
    private static Image _draggingImage;
    private static Point _dragStartCanvas;
    private static double _dragImageStartLeft;
    private static double _dragImageStartTop;
    private static double _worldWidth = 12000;
    private static double _worldHeight = 12000;
    private const double WorldPadding = 200;
    private const double WorldExpandStep = 4000;
    private const double WorldRecoverPadding = 40;
    private static InkCanvasEditingMode _currentMode = InkCanvasEditingMode.Select;
    private static readonly List<ToolbarIconInfo> _modeTools = new List<ToolbarIconInfo>();
    private static ToolbarIconInfo _activeModeTool;

    private static string _apiUrl = "http://127.0.0.1:55557/v1/chat/completions";
    private static string _modelName = "gemini-3-flash";
    private static string _selectionSystemPrompt =
        "You are an image editing agent for canvas workflows. " +
        "When user provides an annotated reference image, you must generate a NEW edited result image based on the instruction and visual cues. " +
        "Do not keep the input unchanged. Do not only describe. Output a generated image result.";
    private static double _zoomLevel = 1.0;
    private static double _lastImageRight = WorldPadding;

    private static readonly string LogDir = @"F:\Desktop\kaifa\huabu";
    private static readonly string LogFile = Path.Combine(LogDir, "client_debug.log");

    private static string F(double v) => v.ToString("0.##");
    private static string P(Point p) => $"({F(p.X)}, {F(p.Y)})";
    private static string R(Rect r) => $"[{F(r.Left)}, {F(r.Top)}, {F(r.Width)}, {F(r.Height)}]";

    private static void LogCanvasState(string tag) {
        try {
            string canvasSize = _mainCanvas == null ? "null" : $"{F(_mainCanvas.Width)}x{F(_mainCanvas.Height)}";
            string overlaySize = _canvasOverlay == null ? "null" : $"{F(_canvasOverlay.Width)}x{F(_canvasOverlay.Height)}";
            Log($"{tag} | zoom={F(_zoomLevel)} pan={P(new Point(_canvasPan.X, _canvasPan.Y))} world={F(_worldWidth)}x{F(_worldHeight)} mainCanvas={canvasSize} overlay={overlaySize} lastImageRight={F(_lastImageRight)}");
        } catch {}
    }

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
        _zoomLevel = 1.0;
        _canvasScale.ScaleX = 1.0;
        _canvasScale.ScaleY = 1.0;
        _canvasPan.X = 0;
        _canvasPan.Y = 0;
        _worldWidth = 12000;
        _worldHeight = 12000;
        _lastImageRight = WorldPadding;
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
        var canvasContainer = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 15, 15)),
            ClipToBounds = true
        };
        _worldTransform = new TransformGroup();
        _worldTransform.Children.Add(_canvasScale);
        _worldTransform.Children.Add(_canvasPan);

        _mainCanvas = new InkCanvas { 
            Background = Brushes.Transparent,
            AllowDrop = true,
            EditingMode = InkCanvasEditingMode.Select,
            RenderTransform = _worldTransform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _mainCanvas.DefaultDrawingAttributes.Color = Colors.Cyan;
        _mainCanvas.SelectionChanged += OnCanvasSelectionChanged;
        _mainCanvas.StrokeCollected += (s, e) => EnsureWorldBoundsForRect(e.Stroke.GetBounds());

        // Keep overlay interactive for its children (popbar), but let empty space pass mouse
        // events through to InkCanvas so drag-selection works.
        _canvasOverlay = new Canvas
        {
            IsHitTestVisible = true,
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        ApplyWorldSize();
        
        canvasContainer.PreviewMouseWheel += (s, e) => {
            if (Keyboard.Modifiers == ModifierKeys.Shift && TransformSelectedImages(scaleDelta: e.Delta > 0 ? 0.05 : -0.05, rotateDelta: 0)) {
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Alt && TransformSelectedImages(scaleDelta: 0, rotateDelta: e.Delta > 0 ? 2 : -2)) {
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control) {
                double delta = e.Delta > 0 ? 0.1 : -0.1;
                ZoomCanvasAt(e.GetPosition(_canvasOverlay), delta);
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
        var clearTool = CreateActionToolbarIcon(pathTrash, "Clear", () => { _mainCanvas.Strokes.Clear(); _mainCanvas.Children.Clear(); _lastImageRight = WorldPadding; HidePopbar(); });

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
        AddMessage("Assistant", "System ready. Select elements and generate. Ctrl+wheel to zoom, Hand tool drag to pan.");
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
        UpdateSelectionPopbarPosition();
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
        Point overlayPoint = e.GetPosition(_canvasOverlay);
        Point canvasPoint = OverlayToCanvasPoint(overlayPoint);
        Log($"MouseDown | overlay={P(overlayPoint)} canvas={P(canvasPoint)} mode={_currentMode}");

        if (_currentMode == InkCanvasEditingMode.None) {
            _isPanningCanvas = true;
            _panStartOverlay = overlayPoint;
            _panStartX = _canvasPan.X;
            _panStartY = _canvasPan.Y;
            _mainCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_currentMode != InkCanvasEditingMode.Select) return;

        if (TryGetImageAtPoint(canvasPoint, out var hitImage)) {
            var selectedElements = _mainCanvas.GetSelectedElements();
            if (!selectedElements.Contains(hitImage) || selectedElements.Count != 1 || _mainCanvas.GetSelectedStrokes().Count > 0) {
                _mainCanvas.Select(new System.Windows.Ink.StrokeCollection(), new List<UIElement> { hitImage });
            }

            double left = InkCanvas.GetLeft(hitImage);
            double top = InkCanvas.GetTop(hitImage);
            _draggingImage = hitImage;
            _isDraggingImage = true;
            _dragStartCanvas = canvasPoint;
            _dragImageStartLeft = double.IsNaN(left) ? 0 : left;
            _dragImageStartTop = double.IsNaN(top) ? 0 : top;
            _mainCanvas.CaptureMouse();
            Log($"DragImage start | imageLeftTop={P(new Point(_dragImageStartLeft, _dragImageStartTop))} pointerCanvas={P(_dragStartCanvas)} imageSize={F(hitImage.Width)}x{F(hitImage.Height)}");
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
        if (_isPanningCanvas) {
            Point overlayPoint = e.GetPosition(_canvasOverlay);
            _canvasPan.X = _panStartX + (overlayPoint.X - _panStartOverlay.X);
            _canvasPan.Y = _panStartY + (overlayPoint.Y - _panStartOverlay.Y);
            UpdateSelectionPopbarPosition();
            e.Handled = true;
            return;
        }

        if (_isDraggingImage && _draggingImage != null) {
            Point overlayPoint = e.GetPosition(_canvasOverlay);
            Point canvasPoint = OverlayToCanvasPoint(overlayPoint);
            double dx = canvasPoint.X - _dragStartCanvas.X;
            double dy = canvasPoint.Y - _dragStartCanvas.Y;
            InkCanvas.SetLeft(_draggingImage, _dragImageStartLeft + dx);
            InkCanvas.SetTop(_draggingImage, _dragImageStartTop + dy);
            EnsureWorldBoundsForRect(GetElementWorldBounds(_draggingImage));
            UpdateSelectionPopbarPosition();
            e.Handled = true;
            return;
        }

        if (!_isMarqueeSelecting) return;

        Point p = e.GetPosition(_canvasOverlay);
        double left = Math.Min(_marqueeStart.X, p.X);
        double top = Math.Min(_marqueeStart.Y, p.Y);
        double width = Math.Abs(p.X - _marqueeStart.X);
        double height = Math.Abs(p.Y - _marqueeStart.Y);

        Canvas.SetLeft(_marqueeRect, left);
        Canvas.SetTop(_marqueeRect, top);
        _marqueeRect.Width = width;
        _marqueeRect.Height = height;
    }

    private static void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        if (_isPanningCanvas) {
            _isPanningCanvas = false;
            _mainCanvas.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_isDraggingImage) {
            if (_draggingImage != null) {
                double imgLeft = InkCanvas.GetLeft(_draggingImage);
                double imgTop = InkCanvas.GetTop(_draggingImage);
                Point screenPos = CanvasToOverlayPoint(new Point(double.IsNaN(imgLeft) ? 0 : imgLeft, double.IsNaN(imgTop) ? 0 : imgTop));
                Log($"DragImage end | imageLeftTop={P(new Point(imgLeft, imgTop))} screenPos={P(screenPos)}");
            }
            _isDraggingImage = false;
            _draggingImage = null;
            _mainCanvas.ReleaseMouseCapture();
            UpdateSelectionPopbarPosition();
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
        Point canvasTopLeft = OverlayToCanvasPoint(new Point(left, top));
        Point canvasBottomRight = OverlayToCanvasPoint(new Point(left + width, top + height));
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

    private static void ZoomCanvasAt(Point overlayPoint, double zoomDelta) {
        double oldZoom = _zoomLevel;
        double nextZoom = Math.Max(0.1, Math.Min(5, oldZoom + zoomDelta));
        if (Math.Abs(nextZoom - oldZoom) < 0.0001) return;

        Point worldPoint = OverlayToCanvasPoint(overlayPoint);
        _zoomLevel = nextZoom;
        _canvasScale.ScaleX = _canvasScale.ScaleY = _zoomLevel;

        Point screenAfter = CanvasToOverlayPoint(worldPoint);
        _canvasPan.X += overlayPoint.X - screenAfter.X;
        _canvasPan.Y += overlayPoint.Y - screenAfter.Y;
        _txtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
        Log($"ZoomCanvasAt | overlay={P(overlayPoint)} world={P(worldPoint)} zoom={F(oldZoom)}->{F(nextZoom)} pan={P(new Point(_canvasPan.X, _canvasPan.Y))}");
    }

    private static Point OverlayToCanvasPoint(Point overlayPoint) {
        Matrix m = _worldTransform.Value;
        if (m.HasInverse) {
            m.Invert();
            return m.Transform(overlayPoint);
        }
        return overlayPoint;
    }

    private static Point CanvasToOverlayPoint(Point canvasPoint) {
        return _worldTransform.Value.Transform(canvasPoint);
    }

    private static void ApplyWorldSize() {
        if (_mainCanvas != null) {
            _mainCanvas.Width = _worldWidth;
            _mainCanvas.Height = _worldHeight;
        }
        if (_canvasOverlay != null) {
            _canvasOverlay.Width = _worldWidth;
            _canvasOverlay.Height = _worldHeight;
        }
        LogCanvasState("ApplyWorldSize");
    }

    private static Rect GetElementWorldBounds(FrameworkElement fe) {
        double x = InkCanvas.GetLeft(fe);
        double y = InkCanvas.GetTop(fe);
        if (double.IsNaN(x)) x = 0;
        if (double.IsNaN(y)) y = 0;
        double w = fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width;
        double h = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;
        if (w <= 0) w = 1;
        if (h <= 0) h = 1;
        return new Rect(x, y, w, h);
    }

    private static void EnsureWorldBoundsForRect(Rect rect) {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;
        Rect original = rect;

        double shiftX = 0;
        double shiftY = 0;
        if (rect.Left < 0) {
            shiftX = -rect.Left + WorldRecoverPadding;
        }
        if (rect.Top < 0) {
            shiftY = -rect.Top + WorldRecoverPadding;
        }
        if (shiftX != 0 || shiftY != 0) {
            ShiftAllWorldContent(shiftX, shiftY);
            rect.Offset(shiftX, shiftY);
            Log($"EnsureWorldBoundsForRect shift | original={R(original)} shiftedBy=({F(shiftX)}, {F(shiftY)}) afterShift={R(rect)}");
        }

        bool grew = false;
        while (rect.Right > _worldWidth - WorldPadding) {
            _worldWidth += WorldExpandStep;
            grew = true;
        }
        while (rect.Bottom > _worldHeight - WorldPadding) {
            _worldHeight += WorldExpandStep;
            grew = true;
        }
        if (grew) {
            ApplyWorldSize();
            Log($"EnsureWorldBoundsForRect grow | rect={R(rect)} world={F(_worldWidth)}x{F(_worldHeight)}");
        }
    }

    private static void ShiftAllWorldContent(double dx, double dy) {
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return;
        Log($"ShiftAllWorldContent start | shift=({F(dx)}, {F(dy)}) children={_mainCanvas?.Children.Count ?? 0} strokes={_mainCanvas?.Strokes.Count ?? 0}");

        foreach (UIElement child in _mainCanvas.Children) {
            if (child is FrameworkElement fe) {
                double left = InkCanvas.GetLeft(fe);
                double top = InkCanvas.GetTop(fe);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                InkCanvas.SetLeft(fe, left + dx);
                InkCanvas.SetTop(fe, top + dy);
            }
        }

        foreach (var stroke in _mainCanvas.Strokes) {
            var points = stroke.StylusPoints;
            for (int i = 0; i < points.Count; i++) {
                var p = points[i];
                points[i] = new StylusPoint(p.X + dx, p.Y + dy, p.PressureFactor);
            }
        }

        _lastImageRight += dx;
        if (_isDraggingImage) {
            _dragImageStartLeft += dx;
            _dragImageStartTop += dy;
            _dragStartCanvas = new Point(_dragStartCanvas.X + dx, _dragStartCanvas.Y + dy);
        }
        _canvasPan.X -= dx * _zoomLevel;
        _canvasPan.Y -= dy * _zoomLevel;
        LogCanvasState("ShiftAllWorldContent end");
    }

    private static bool TryGetImageAtPoint(Point canvasPoint, out Image hitImage) {
        hitImage = null;
        HitTestResult hit = VisualTreeHelper.HitTest(_mainCanvas, canvasPoint);
        DependencyObject current = hit?.VisualHit;
        while (current != null) {
            if (current is Image img && _mainCanvas.Children.Contains(img)) {
                hitImage = img;
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static bool TransformSelectedImages(double scaleDelta, double rotateDelta) {
        var selectedImages = _mainCanvas.GetSelectedElements().OfType<Image>().ToList();
        if (selectedImages.Count == 0) return false;

        foreach (var img in selectedImages) {
            var group = EnsureImageTransformGroup(img, out var scale, out var rotate);
            if (scaleDelta != 0) {
                double nextX = Math.Max(0.1, Math.Min(10, scale.ScaleX + scaleDelta));
                double nextY = Math.Max(0.1, Math.Min(10, scale.ScaleY + scaleDelta));
                scale.ScaleX = nextX;
                scale.ScaleY = nextY;
            }
            if (rotateDelta != 0) {
                rotate.Angle += rotateDelta;
            }
            img.RenderTransform = group;
            EnsureWorldBoundsForRect(GetElementWorldBounds(img));
        }

        UpdateSelectionPopbarPosition();
        return true;
    }

    private static TransformGroup EnsureImageTransformGroup(Image img, out ScaleTransform scale, out RotateTransform rotate) {
        scale = null;
        rotate = null;

        if (img.RenderTransform is TransformGroup existingGroup) {
            foreach (var t in existingGroup.Children) {
                if (t is ScaleTransform s && scale == null) scale = s;
                if (t is RotateTransform r && rotate == null) rotate = r;
            }
            if (scale != null && rotate != null) {
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                return existingGroup;
            }
        }

        var group = new TransformGroup();
        scale = new ScaleTransform(1, 1);
        rotate = new RotateTransform(0);

        if (img.RenderTransform is TransformGroup oldGroup) {
            foreach (var t in oldGroup.Children) group.Children.Add(t);
        } else if (img.RenderTransform != null && !(img.RenderTransform is MatrixTransform m && m.Matrix.IsIdentity)) {
            group.Children.Add(img.RenderTransform);
        }

        group.Children.Add(scale);
        group.Children.Add(rotate);
        img.RenderTransformOrigin = new Point(0.5, 0.5);
        return group;
    }

    private static void UpdateSelectionPopbarPosition() {
        var selectedElements = _mainCanvas.GetSelectedElements();
        var selectedStrokes = _mainCanvas.GetSelectedStrokes();
        if (selectedElements.Count == 0 && selectedStrokes.Count == 0) {
            HidePopbar();
            return;
        }

        Rect bounds = Rect.Empty;
        foreach (var el in selectedElements) {
            if (el is FrameworkElement fe) {
                double left = InkCanvas.GetLeft(fe);
                double top = InkCanvas.GetTop(fe);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                double width = fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width;
                double height = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;
                if (width > 0 && height > 0) bounds.Union(new Rect(left, top, width, height));
            }
        }
        foreach (var stroke in selectedStrokes) bounds.Union(stroke.GetBounds());
        if (bounds.IsEmpty) return;

        Point canvasPos = CanvasToOverlayPoint(new Point(bounds.Left, bounds.Bottom));
        Canvas.SetLeft(_selectionPopbar, Math.Max(10, canvasPos.X));
        Canvas.SetTop(_selectionPopbar, Math.Max(10, canvasPos.Y + 10));
        _selectionPopbar.Visibility = Visibility.Visible;
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
        _lastImageRight = WorldPadding;
        foreach (UIElement child in _mainCanvas.Children) {
            if (child is Image img) {
                double left = InkCanvas.GetLeft(img);
                if (double.IsNaN(left)) left = 0;
                double width = img.ActualWidth > 0 ? img.ActualWidth : img.Width;
                if (width <= 0) width = 400;
                _lastImageRight = Math.Max(_lastImageRight, left + width + 20);
            }
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
            LogCanvasState("AddImageToCanvas start");
            Log("AddImageToCanvas source | path=" + path);
            var b = new BitmapImage(new Uri(path, UriKind.RelativeOrAbsolute));
            double maxWidth = 400;
            double sourceWidth = b.PixelWidth > 0 ? b.PixelWidth : maxWidth;
            double sourceHeight = b.PixelHeight > 0 ? b.PixelHeight : maxWidth;
            double drawWidth = Math.Min(maxWidth, sourceWidth);
            double drawHeight = sourceHeight * (drawWidth / sourceWidth);
            var img = new Image {
                Source = b,
                Width = drawWidth,
                Height = drawHeight,
                Stretch = Stretch.Fill
            };
            Point visibleAnchor = OverlayToCanvasPoint(new Point(120, 120));
            double spawnX = Math.Max(_lastImageRight, visibleAnchor.X);
            double spawnY = visibleAnchor.Y;
            Log($"AddImageToCanvas place | pixel={sourceWidth}x{sourceHeight} draw={F(drawWidth)}x{F(drawHeight)} visibleAnchor={P(visibleAnchor)} spawn={P(new Point(spawnX, spawnY))}");
            InkCanvas.SetLeft(img, spawnX);
            InkCanvas.SetTop(img, spawnY);
            _mainCanvas.Children.Add(img);
            EnsureWorldBoundsForRect(GetElementWorldBounds(img));
            _lastImageRight = Math.Max(_lastImageRight, InkCanvas.GetLeft(img) + drawWidth + 20);
            var finalLeft = InkCanvas.GetLeft(img);
            var finalTop = InkCanvas.GetTop(img);
            var finalScreen = CanvasToOverlayPoint(new Point(finalLeft, finalTop));
            Log($"AddImageToCanvas done | finalWorld={P(new Point(finalLeft, finalTop))} finalScreen={P(finalScreen)} nextLastImageRight={F(_lastImageRight)}");
            LogCanvasState("AddImageToCanvas end");
            var selectTool = _modeTools.FirstOrDefault(t => t.Mode == InkCanvasEditingMode.Select);
            if (selectTool != null) ActivateModeTool(selectTool);
            else ApplyCanvasMode(InkCanvasEditingMode.Select);
        } catch (Exception ex) {
            Log("AddImageToCanvas Error: " + ex.Message + " | path=" + path);
        }
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
