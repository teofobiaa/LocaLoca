using System.Collections.ObjectModel;
using System.Diagnostics;
using LocaLoca.Models;
using LocaLoca.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Graphics;
using WinRT.Interop;

namespace LocaLoca;

public sealed partial class MainWindow : Window
{
    private readonly LocaLocaApiClient _api = new();
    private readonly ObservableCollection<AttractionDto> _attractions = new();
    private readonly ObservableCollection<VisitDto> _visits = new();
    private readonly ObservableCollection<AttractionDto> _wishlistAttractions = new();
    private readonly HashSet<int> _wishlistIds = new();
    private List<AttractionDto> _allAttractions = new();
    private UserDto? _currentUser;
    private AttractionDto? _selectedAttraction;
    private VisitDto? _selectedVisit;
    private List<CategoryDto> _categories = new();
    private bool _isRegisterMode;
    private bool _initialized;
    private bool _mapInitialized;
    private bool _isDarkTheme;
    private string _authStatusTone = "muted";
    private string _profileStatusTone = "muted";

    public ObservableCollection<AttractionDto> Attractions => _attractions;
    public ObservableCollection<VisitDto> Visits => _visits;
    public ObservableCollection<AttractionDto> WishlistAttractions => _wishlistAttractions;
    public MainWindow()
    {
        InitializeComponent();
        LoadThemePreference();
        Title = "Loca Loca";
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // Keep the initial window fully inside the active monitor's work area.
        // This also handles smaller displays and work areas reduced by the taskbar.
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        const int edgePadding = 24;
        var width = Math.Min(1360, Math.Max(1, workArea.Width - edgePadding * 2));
        var height = Math.Min(880, Math.Max(1, workArea.Height - edgePadding * 2));
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + (workArea.Height - height) / 2;

        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "loca-loca.ico"));
        Closed += MainWindow_Closed;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await InitializeConnectionAsync();
    }

    private async Task InitializeConnectionAsync()
    {
        SetAuthStatus("Подключаем локальный API и облачную PostgreSQL…", "muted");
        try
        {
            var health = await _api.EnsureApiAndDatabaseAsync();
            SetAuthStatus("Облачная база подключена. Можно войти или создать аккаунт.", "success");
            RetryConnectionButton.Visibility = Visibility.Collapsed;
            ConnectionStatusText.Text = "Облачная база подключена";
        }
        catch (Exception ex)
        {
            SetAuthStatus(ex.Message, "error");
            RetryConnectionButton.Visibility = Visibility.Visible;
            ConnectionStatusText.Text = "Нет подключения";
        }
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        RetryConnectionButton.IsEnabled = false;
        try { await InitializeConnectionAsync(); }
        finally { RetryConnectionButton.IsEnabled = true; }
    }

    private async void AuthSubmit_Click(object sender, RoutedEventArgs e)
    {
        AuthSubmitButton.IsEnabled = false;
        SetAuthStatus(_isRegisterMode ? "Создаём аккаунт…" : "Проверяем данные…", "muted");
        try
        {
            var login = LoginBox.Text.Trim();
            var password = PasswordBox.Password;
            AuthResponse result;
            if (_isRegisterMode)
            {
                result = await _api.RegisterAsync(login, password, DisplayNameBox.Text.Trim());
            }
            else
            {
                result = await _api.LoginAsync(login, password);
            }

            _currentUser = result.User;
            ProfileNameText.Text = result.User.DisplayName;
            AuthPage.Visibility = Visibility.Collapsed;
            MainShell.Visibility = Visibility.Visible;
            ShowPage("home");
            await LoadMainDataAsync();
        }
        catch (Exception ex)
        {
            SetAuthStatus(ex.Message, "error");
        }
        finally
        {
            AuthSubmitButton.IsEnabled = true;
        }
    }

    private void AuthModeButton_Click(object sender, RoutedEventArgs e)
    {
        _isRegisterMode = !_isRegisterMode;
        DisplayNamePanel.Visibility = _isRegisterMode ? Visibility.Visible : Visibility.Collapsed;
        AuthEyebrow.Text = _isRegisterMode ? "Твоё путешествие начинается" : "С возвращением";
        AuthTitle.Text = _isRegisterMode ? "Создай свой аккаунт" : "С возвращением";
        AuthSubtitle.Text = _isRegisterMode ? "Сохраняй места, фотографии и впечатления." : "Войди, чтобы продолжить свою историю.";
        AuthSubmitButton.Content = _isRegisterMode ? "Создать аккаунт" : "Войти в аккаунт";
        AuthModeButton.Content = _isRegisterMode ? "У меня уже есть аккаунт" : "Создать новый аккаунт";
        SetAuthStatus(string.Empty, "muted");
    }

    private async Task LoadMainDataAsync()
    {
        try
        {
            var categoriesTask = _api.GetCategoriesAsync();
            var catalogTask = _api.GetCatalogAsync();
            var visitsTask = _api.GetVisitsAsync();
            var wishlistTask = _api.GetWishlistAsync();
            var summaryTask = _api.GetSummaryAsync();
            await Task.WhenAll(categoriesTask, catalogTask, visitsTask, wishlistTask, summaryTask);

            _categories = await categoriesTask;
            _allAttractions = await catalogTask;
            _attractions.Clear();
            foreach (var item in _allAttractions) _attractions.Add(item);

            _visits.Clear();
            foreach (var item in await visitsTask) _visits.Add(item);

            _wishlistIds.Clear();
            foreach (var id in await wishlistTask) _wishlistIds.Add(id);
            RefreshWishlistCollection();
            PopulateCategories();

            var summary = await summaryTask;
            VisitsCountText.Text = summary.VisitCount.ToString();
            PlacesCountText.Text = summary.DistinctAttractions.ToString();
            CitiesCountText.Text = summary.Cities.ToString();
            CountriesCountText.Text = summary.Countries.ToString();
            DashboardAttractionsList.ItemsSource = _allAttractions.Take(4).ToList();
            ConnectionStatusText.Text = "Облачная база подключена";
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Не удалось загрузить данные", ex.Message);
        }
    }

    private void PopulateCategories()
    {
        CategoryCombo.Items.Clear();
        CategoryCombo.Items.Add(new ComboBoxItem { Content = "Все категории", Tag = null });
        foreach (var category in _categories)
            CategoryCombo.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });
        CategoryCombo.SelectedIndex = 0;
    }

    private void RefreshWishlistCollection()
    {
        _wishlistAttractions.Clear();
        foreach (var attraction in _allAttractions.Where(a => _wishlistIds.Contains(a.Id)))
            _wishlistAttractions.Add(attraction);
        RemoveWishlistButton.IsEnabled = WishlistList.SelectedItem is AttractionDto;
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchCatalogAsync();

    private async Task SearchCatalogAsync()
    {
        try
        {
            int? categoryId = null;
            if (CategoryCombo.SelectedItem is ComboBoxItem { Tag: int id }) categoryId = id;
            var items = await _api.GetCatalogAsync(SearchBox.Text, categoryId);
            _attractions.Clear();
            foreach (var item in items) _attractions.Add(item);
            RefreshWishlistCollection();
            DashboardAttractionsList.ItemsSource = _attractions.Take(4).ToList();
            if (_selectedAttraction is not null)
            {
                _selectedAttraction = _attractions.FirstOrDefault(a => a.Id == _selectedAttraction.Id);
                UpdateAttractionDetails(_selectedAttraction);
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Ошибка поиска", ex.Message);
        }
    }

    private void CatalogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedAttraction = CatalogList.SelectedItem as AttractionDto;
        UpdateAttractionDetails(_selectedAttraction);
    }

    private void WishlistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveWishlistButton.IsEnabled = WishlistList.SelectedItem is AttractionDto;
    }

    private void DashboardAttraction_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AttractionDto attraction) return;
        ShowPage("catalog");
        SelectAttraction(attraction);
    }

    private void UpdateAttractionDetails(AttractionDto? attraction)
    {
        var hasSelection = attraction is not null;
        SelectedAttractionName.Text = hasSelection ? attraction!.Name : "Твоя следующая история";
        SelectedAttractionCategory.Text = hasSelection ? attraction!.Category : "Выбери место в каталоге";
        SelectedAttractionLocation.Text = hasSelection ? $"{attraction!.City} · {attraction.Country}" : "Город · Страна";
        SelectedAttractionDescription.Text = hasSelection ? attraction!.Description : "Выбери достопримечательность слева, чтобы посмотреть описание и историческую справку.";
        SelectedAttractionHistory.Text = hasSelection ? attraction!.History : string.Empty;
        AddVisitButton.IsEnabled = hasSelection;
        WishlistButton.IsEnabled = hasSelection;
        WishlistButton.Content = hasSelection && _wishlistIds.Contains(attraction!.Id) ? "♥  Убрать из желаний" : "♡  Хочу посетить";
    }

    private void SelectAttraction(AttractionDto attraction)
    {
        _selectedAttraction = attraction;
        UpdateAttractionDetails(attraction);
        CatalogList.SelectedItem = _attractions.FirstOrDefault(a => a.Id == attraction.Id);
    }

    private async void Wishlist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAttraction is null) return;
        try
        {
            if (_wishlistIds.Contains(_selectedAttraction.Id))
            {
                await _api.RemoveFromWishlistAsync(_selectedAttraction.Id);
                _wishlistIds.Remove(_selectedAttraction.Id);
            }
            else
            {
                await _api.AddToWishlistAsync(_selectedAttraction.Id);
                _wishlistIds.Add(_selectedAttraction.Id);
            }
            UpdateAttractionDetails(_selectedAttraction);
            RefreshWishlistCollection();
        }
        catch (Exception ex) { await ShowMessageAsync("Не удалось обновить список", ex.Message); }
    }

    private async void RemoveWishlist_Click(object sender, RoutedEventArgs e)
    {
        if (WishlistList.SelectedItem is not AttractionDto attraction) return;
        try
        {
            await _api.RemoveFromWishlistAsync(attraction.Id);
            _wishlistIds.Remove(attraction.Id);
            RefreshWishlistCollection();
        }
        catch (Exception ex) { await ShowMessageAsync("Не удалось удалить место", ex.Message); }
    }

    private async void AddVisit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAttraction is null) return;
        await ShowVisitDialogAsync(_selectedAttraction, null);
    }

    private async void NewMapVisit_Click(object sender, RoutedEventArgs e) => await OpenMapAsync(startNewVisit: true);

    private async void NewVisitFromCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAttraction is null)
        {
            ShowPage("catalog");
            await ShowMessageAsync("Выбери место", "Сначала найди достопримечательность в каталоге.");
            return;
        }
        await ShowVisitDialogAsync(_selectedAttraction, null);
    }

    private void VisitsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedVisit = VisitsList.SelectedItem as VisitDto;
        UpdateVisitDetails(_selectedVisit);
        if (_selectedVisit is not null) _ = LoadPhotosForVisitAsync(_selectedVisit.Id);
        else PhotosList.ItemsSource = null;
    }

    private void UpdateVisitDetails(VisitDto? visit)
    {
        var enabled = visit is not null;
        SelectedVisitName.Text = enabled ? visit!.AttractionName : "Выбери запись";
        SelectedVisitMeta.Text = enabled
            ? visit!.UserMapPlaceId.HasValue ? $"{visit.VisitDate} · Личная точка" : $"{visit.VisitDate} · {visit.City} · {visit.Country}"
            : "Дата · Город · Страна";
        SelectedVisitRating.Text = enabled ? $"Оценка: {visit!.Rating}/5" : "Оценка: —";
        SelectedVisitImpressions.Text = enabled && !string.IsNullOrWhiteSpace(visit!.Impressions) ? visit.Impressions : "Впечатления пока не добавлены.";
        SelectedVisitNotes.Text = enabled ? visit!.Notes : string.Empty;
        EditVisitButton.IsEnabled = enabled;
        DeleteVisitButton.IsEnabled = enabled;
    }

    private async Task LoadPhotosForVisitAsync(int visitId)
    {
        try
        {
            var visit = _visits.FirstOrDefault(item => item.Id == visitId);
            if (visit is null || _selectedVisit?.Id != visitId) return;

            var photos = visit.UserMapPlaceId is int placeId
                ? await _api.GetMapPlacePhotosAsync(placeId)
                : await _api.GetPhotosAsync(visitId);

            // A slower response for the previous selection must not replace
            // the photos belonging to the visit currently shown in the UI.
            if (_selectedVisit?.Id == visitId)
                PhotosList.ItemsSource = photos;
        }
        catch (Exception ex)
        {
            if (_selectedVisit?.Id == visitId)
                await ShowMessageAsync("Не удалось загрузить фотографии", ex.Message);
        }
    }

    private async void EditVisit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVisit is null) return;
        if (_selectedVisit.UserMapPlaceId is not null)
        {
            await ShowMapPlaceVisitDialogAsync(_selectedVisit);
            return;
        }
        var attraction = _allAttractions.FirstOrDefault(a => a.Id == _selectedVisit.AttractionId);
        if (attraction is null)
        {
            await ShowMessageAsync("Место не найдено", "Не удалось найти достопримечательность в каталоге.");
            return;
        }
        await ShowVisitDialogAsync(attraction, _selectedVisit);
    }

    private async void DeleteVisit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVisit is null) return;
        var result = await ConfirmAsync("Удалить посещение?", $"Запись «{_selectedVisit.AttractionName}» и связанные фотографии будут удалены.");
        if (result != ContentDialogResult.Primary) return;
        try
        {
            if (_selectedVisit.UserMapPlaceId is int placeId)
                await _api.DeleteMapPlaceAsync(placeId);
            else
                await _api.DeleteVisitAsync(_selectedVisit.Id);
            _selectedVisit = null;
            await LoadMainDataAsync();
            UpdateVisitDetails(null);
            PhotosList.ItemsSource = null;
        }
        catch (Exception ex) { await ShowMessageAsync("Не удалось удалить посещение", ex.Message); }
    }

    private async Task ShowMapPlaceVisitDialogAsync(VisitDto existing)
    {
        var placeId = existing.UserMapPlaceId!.Value;
        var nameBox = new TextBox { Text = existing.AttractionName, MaxLength = 150, PlaceholderText = "Название места" };
        var datePicker = new DatePicker
        {
            MaxYear = DateTimeOffset.Now,
            Date = new DateTimeOffset(DateTime.Parse(existing.VisitDate).Date)
        };
        var ratingPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in new[] { "1 — Не понравилось", "2 — Так себе", "3 — Нормально", "4 — Очень понравилось", "5 — Незабываемо" })
            ratingPicker.Items.Add(value);
        ratingPicker.SelectedIndex = Math.Clamp(existing.Rating - 1, 0, 4);
        var impressionsBox = new TextBox { Text = existing.Impressions, PlaceholderText = "Что запомнилось?", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
        var notesBox = new TextBox { Text = existing.Notes, PlaceholderText = "Личные заметки", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70 };
        var photoLabel = new TextBlock { Text = "Фотографии не выбраны", FontSize = 12, Foreground = Brush("#829087"), TextWrapping = TextWrapping.Wrap };
        var pickedPhotos = new List<StorageFile>();
        var addPhotosButton = new Button { Content = "＋  Добавить фотографии", HorizontalAlignment = HorizontalAlignment.Left };
        addPhotosButton.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" }) picker.FileTypeFilter.Add(ext);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;
            pickedPhotos.AddRange(files);
            photoLabel.Text = $"Выбрано новых фотографий: {pickedPhotos.Count}. За одну операцию — до 10 файлов и 20 МиБ суммарно.";
        };

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "Название места", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(nameBox);
        content.Children.Add(new TextBlock { Text = "Дата посещения", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(datePicker);
        content.Children.Add(new TextBlock { Text = "Оценка", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(ratingPicker);
        content.Children.Add(new TextBlock { Text = "Впечатления", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(impressionsBox);
        content.Children.Add(new TextBlock { Text = "Заметки", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(notesBox);
        content.Children.Add(addPhotosButton);
        content.Children.Add(photoLabel);

        var dialog = new ContentDialog
        {
            Title = "Редактировать личное посещение",
            Content = content,
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = Root.RequestedTheme,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(nameBox.Text) || ratingPicker.SelectedIndex < 0)
        {
            await ShowMessageAsync("Проверь данные", "Укажи название места и оценку посещения.");
            return;
        }
        try
        {
            await _api.UpdateMapPlaceVisitAsync(placeId, nameBox.Text.Trim(), datePicker.Date.DateTime,
                ratingPicker.SelectedIndex + 1, impressionsBox.Text.Trim(), notesBox.Text.Trim());
            await _api.UploadMapPlacePhotosAsync(placeId, pickedPhotos);
            await LoadMainDataAsync();
            var updated = _visits.FirstOrDefault(v => v.UserMapPlaceId == placeId);
            if (updated is not null) VisitsList.SelectedItem = updated;
        }
        catch (Exception ex) { await ShowMessageAsync("Не удалось сохранить посещение", ex.Message); }
    }

    private async Task ShowVisitDialogAsync(AttractionDto attraction, VisitDto? existing)
    {
        var datePicker = new DatePicker
        {
            MaxYear = DateTimeOffset.Now,
            Date = existing is null ? DateTimeOffset.Now : new DateTimeOffset(DateTime.Parse(existing.VisitDate).Date)
        };
        var ratingPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in new[] { "1 — Не понравилось", "2 — Так себе", "3 — Нормально", "4 — Очень понравилось", "5 — Незабываемо" })
            ratingPicker.Items.Add(value);
        ratingPicker.SelectedIndex = existing is null ? 4 : Math.Clamp(existing.Rating - 1, 0, 4);
        var impressionsBox = new TextBox { Text = existing?.Impressions ?? string.Empty, PlaceholderText = "Что запомнилось?", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
        var notesBox = new TextBox { Text = existing?.Notes ?? string.Empty, PlaceholderText = "Заметки: с кем был, полезные детали…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70 };
        var photoLabel = new TextBlock { Text = "Фотографии не выбраны", FontSize = 12, Foreground = Brush("#829087"), TextWrapping = TextWrapping.Wrap };
        var pickedPhotos = new List<StorageFile>();
        var addPhotosButton = new Button { Content = "＋  Добавить фотографии", HorizontalAlignment = HorizontalAlignment.Left };
        addPhotosButton.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" }) picker.FileTypeFilter.Add(ext);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;
            pickedPhotos.AddRange(files);
            photoLabel.Text = $"Выбрано новых фотографий: {pickedPhotos.Count}. За одно сохранение — до 10 файлов, не более 20 МБ суммарно.";
        };

        var content = new StackPanel { Spacing = 11 };
        content.Children.Add(new TextBlock
        {
            Text = $"{attraction.Name} · {attraction.City}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("#263A31"),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock { Text = "Дата посещения", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(datePicker);
        content.Children.Add(new TextBlock { Text = "Оценка", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(ratingPicker);
        content.Children.Add(new TextBlock { Text = "Впечатления", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(impressionsBox);
        content.Children.Add(new TextBlock { Text = "Заметки", FontSize = 12, Foreground = Brush("#6F7D74") });
        content.Children.Add(notesBox);
        content.Children.Add(addPhotosButton);
        content.Children.Add(photoLabel);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "Новое посещение" : "Редактирование посещения",
            Content = content,
            PrimaryButtonText = "Сохранить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = Root.RequestedTheme,
            XamlRoot = Root.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (ratingPicker.SelectedIndex < 0)
        {
            await ShowMessageAsync("Проверь данные", "Выбери оценку посещения.");
            return;
        }
        try
        {
            var savedResult = await _api.SaveVisitAsync(attraction.Id, existing?.Id, datePicker.Date.DateTime,
                ratingPicker.SelectedIndex + 1, impressionsBox.Text.Trim(), notesBox.Text.Trim(), pickedPhotos);
            await LoadMainDataAsync();
            ShowPage("visits");
            var saved = _visits.FirstOrDefault(v => v.Id == savedResult.Id);
            if (saved is not null) VisitsList.SelectedItem = saved;
        }
        catch (Exception ex) { await ShowMessageAsync("Не удалось сохранить посещение", ex.Message); }
    }

    private async void OpenPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not PhotoDto photo) return;
        try
        {
            var bytes = _selectedVisit?.UserMapPlaceId is not null
                ? await _api.GetMapPlacePhotoBytesAsync(photo.Id)
                : await _api.GetPhotoBytesAsync(photo.Id);
            var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLoca", "Temp");
            Directory.CreateDirectory(tempDir);
            var extension = Path.GetExtension(photo.FilePath);
            var path = Path.Combine(tempDir, $"photo-{photo.Id}-{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(path, bytes);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { await ShowMessageAsync("Не удалось открыть фотографию", ex.Message); }
    }

    private async void DeletePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not PhotoDto photo || _selectedVisit is null) return;
        var result = await ConfirmAsync("Удалить фотографию?", "Фотография будет удалена из облачной базы данных.");
        if (result != ContentDialogResult.Primary) return;
        try
        {
            if (_selectedVisit.UserMapPlaceId is not null)
                await _api.DeleteMapPlacePhotoAsync(photo.Id);
            else
                await _api.DeletePhotoAsync(photo.Id);
            await LoadPhotosForVisitAsync(_selectedVisit.Id);
        }
        catch (Exception ex) { await ShowMessageAsync("Не удалось удалить фотографию", ex.Message); }
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowPage("home");
    private async void CatalogNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("catalog");
        if (_attractions.Count == 0) await SearchCatalogAsync();
    }
    private async void VisitsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("visits");
        await LoadMainDataAsync();
    }
    private void WishlistNav_Click(object sender, RoutedEventArgs e) => ShowPage("wishlist");

    private async void MapNav_Click(object sender, RoutedEventArgs e) => await OpenMapAsync();

    private async Task OpenMapAsync(bool startNewVisit = false)
    {
        ShowPage("map");
        try
        {
            if (!_mapInitialized)
            {
                var webViewDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LocaLoca",
                    "WebView2");
                Directory.CreateDirectory(webViewDataPath);
                var webViewEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    null,
                    webViewDataPath,
                    new CoreWebView2EnvironmentOptions());
                await MapWebView.EnsureCoreWebView2Async(webViewEnvironment);
                MapWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _mapInitialized = true;
            }

            MapWebView.Source = _api.GetMapUri(startNewVisit, _isDarkTheme);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Не удалось открыть карту", "Проверь наличие Microsoft Edge WebView2 Runtime и доступ к интернету. " + ex.Message);
        }
    }

    private void ShowPage(string page)
    {
        DashboardPage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        CatalogPage.Visibility = page == "catalog" ? Visibility.Visible : Visibility.Collapsed;
        VisitsPage.Visibility = page == "visits" ? Visibility.Visible : Visibility.Collapsed;
        WishlistPage.Visibility = page == "wishlist" ? Visibility.Visible : Visibility.Collapsed;
        MapPage.Visibility = page == "map" ? Visibility.Visible : Visibility.Collapsed;
        ProfilePage.Visibility = page == "profile" ? Visibility.Visible : Visibility.Collapsed;
        SupportPage.Visibility = page == "support" ? Visibility.Visible : Visibility.Collapsed;

        (PageEyebrow.Text, PageTitle.Text) = page switch
        {
            "home" => ("Личная карта воспоминаний", $"Привет, {_currentUser?.DisplayName ?? "путешественник"}"),
            "catalog" => ("Места для новых историй", "Каталог мест"),
            "visits" => ("Сохранённые моменты", "Мои посещения"),
            "wishlist" => ("Идеи для следующей поездки", "Хочу посетить"),
            "map" => ("Исследуй мир", "Карта открытий"),
            "profile" => ("Личные данные и оформление", "Профиль и настройки"),
            "support" => ("Контакты и полезные ссылки", "Поддержка"),
            _ => ("Исследуй мир", "Карта открытий")
        };
        HomeNavButton.Background = page == "home" ? Brush("#29443D") : Brush("Transparent");
        SupportNavButton.Background = page == "support" ? Brush("#29443D") : Brush("Transparent");
    }

    private void SupportNav_Click(object sender, RoutedEventArgs e) => ShowPage("support");

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null) return;

        ProfileDisplayNameBox.Text = _currentUser.DisplayName;
        ProfileLoginText.Text = _currentUser.Login;
        ProfileThemeToggle.IsOn = _isDarkTheme;
        SetProfileStatus(string.Empty, "muted");
        ShowPage("profile");
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null) return;

        var newName = ProfileDisplayNameBox.Text.Trim();
        if (newName.Length is < 1 or > 40)
        {
            SetProfileStatus("Имя должно содержать от 1 до 40 символов.", "error");
            return;
        }
        if (string.Equals(newName, _currentUser.DisplayName, StringComparison.Ordinal))
        {
            SetProfileStatus("Изменений нет.", "muted");
            return;
        }

        ProfileSaveButton.IsEnabled = false;
        SetProfileStatus("Сохраняем изменения…", "muted");
        try
        {
            var updatedUser = await _api.UpdateDisplayNameAsync(newName);
            _currentUser = updatedUser;
            ProfileNameText.Text = updatedUser.DisplayName;
            ProfileLoginText.Text = updatedUser.Login;
            ProfileDisplayNameBox.Text = updatedUser.DisplayName;
            SetProfileStatus("Имя профиля успешно обновлено.", "success");
        }
        catch (Exception ex)
        {
            SetProfileStatus(ex.Message, "error");
        }
        finally
        {
            ProfileSaveButton.IsEnabled = true;
        }
    }

    private void SetProfileStatus(string message, string tone)
    {
        _profileStatusTone = tone;
        ProfileStatusText.Text = message;
        ProfileStatusText.Foreground = Brush(tone switch
        {
            "success" => "#4F8758",
            "error" => "#B85D4D",
            _ => "#7A8980"
        });
    }

    private async void ProfileTheme_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch themeToggle) return;

        _isDarkTheme = themeToggle.IsOn;
        ApplyRequestedTheme();
        SetAuthStatus(AuthStatusText.Text, _authStatusTone);
        SetProfileStatus(ProfileStatusText.Text, _profileStatusTone);
        SaveThemePreference();
        await UpdateMapThemeAsync();
    }

    private async Task UpdateMapThemeAsync()
    {
        if (!_mapInitialized || MapWebView.CoreWebView2 is null) return;
        var theme = _isDarkTheme ? "dark" : "light";
        try
        {
            await MapWebView.CoreWebView2.ExecuteScriptAsync($"document.documentElement.dataset.theme = '{theme}';");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Не удалось обновить тему карты: {ex.Message}");
        }
    }

    private static string ThemePreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocaLoca",
        "theme-preference.txt");

    private void LoadThemePreference()
    {
        try
        {
            _isDarkTheme = File.Exists(ThemePreferencePath) &&
                string.Equals(File.ReadAllText(ThemePreferencePath).Trim(), "dark", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            _isDarkTheme = false;
        }
        catch (UnauthorizedAccessException)
        {
            _isDarkTheme = false;
        }

        ApplyRequestedTheme();
    }

    private void ApplyRequestedTheme()
    {
        var theme = _isDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        Root.RequestedTheme = theme;
        AuthPage.RequestedTheme = theme;
        MainShell.RequestedTheme = theme;
    }

    private void SaveThemePreference()
    {
        try
        {
            var directory = Path.GetDirectoryName(ThemePreferencePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(ThemePreferencePath, _isDarkTheme ? "dark" : "light");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Не удалось сохранить настройку темы: {ex.Message}");
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e) => await LogoutAsync();

    private async Task LogoutAsync()
    {
        try { await _api.LogoutAsync(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Server logout failed: {ex.Message}");
            _api.ClearLocalSession();
        }
        if (_mapInitialized && MapWebView.CoreWebView2 is not null)
        {
            try
            {
                await MapWebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
                MapWebView.NavigateToString("<!doctype html><html><body></body></html>");
            }
            catch (Exception ex) { Debug.WriteLine($"WebView2 cleanup failed: {ex.Message}"); }
        }
        _currentUser = null;
        _selectedAttraction = null;
        _selectedVisit = null;
        PasswordBox.Password = string.Empty;
        LoginBox.Text = string.Empty;
        DisplayNameBox.Text = string.Empty;
        _isRegisterMode = false;
        DisplayNamePanel.Visibility = Visibility.Collapsed;
        AuthEyebrow.Text = "С возвращением";
        AuthTitle.Text = "С возвращением";
        AuthSubtitle.Text = "Войди, чтобы продолжить свою историю.";
        AuthSubmitButton.Content = "Войти в аккаунт";
        AuthModeButton.Content = "Создать новый аккаунт";
        ShowPage("home");
        MainShell.Visibility = Visibility.Collapsed;
        AuthPage.Visibility = Visibility.Visible;
        SetAuthStatus("Облачная база готова. Войди в аккаунт.", "muted");
    }

    private async Task<ContentDialogResult> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = Root.RequestedTheme,
            XamlRoot = Root.XamlRoot
        };
        return await dialog.ShowAsync();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "Понятно",
            RequestedTheme = Root.RequestedTheme,
            XamlRoot = Root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void SetAuthStatus(string message, string tone)
    {
        _authStatusTone = tone;
        AuthStatusText.Text = message;
        AuthStatusText.Foreground = Brush(tone switch
        {
            "success" => "#4F8758",
            "error" => "#B85D4D",
            _ => "#7A8980"
        });
    }

    private SolidColorBrush Brush(string color)
    {
        if (string.Equals(color, "Transparent", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        if (_isDarkTheme)
        {
            color = color.ToUpperInvariant() switch
            {
                "#7A8980" => "#A5B2A9",
                "#4F8758" => "#7ECF8A",
                "#B85D4D" => "#E9A095",
                "#829087" => "#A3B0A7",
                "#6F7D74" => "#A8B6AC",
                "#263A31" => "#DDE8E0",
                "#29443D" => "#2B4438",
                "#718078" => "#A8B5AB",
                _ => color
            };
        }

        var hex = color.TrimStart('#');
        if (hex.Length != 6) throw new ArgumentException("Цвет должен иметь формат #RRGGBB.", nameof(color));
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16)));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args) => _api.Dispose();
}
