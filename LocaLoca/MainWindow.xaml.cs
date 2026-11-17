using System.Collections.ObjectModel;
using System.Diagnostics;
using LocaLoca.Models;
using LocaLoca.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Graphics;
using WinRT.Interop;

namespace LocaLoca;

public sealed partial class MainWindow : Window
{
    private readonly LocaLocaApiClient _api = new();
    private readonly ObservableCollection<AttractionDto> _attractions = new();
    private readonly ObservableCollection<VisitDto> _visits = new();
    private readonly ObservableCollection<AttractionDto> _wishlistAttractions = new();
    private readonly ObservableCollection<BirthdayContactDto> _birthdayContacts = new();
    private int? _editingBirthdayContactId;
    private readonly HashSet<int> _wishlistIds = new();
    private readonly Dictionary<int, AttractionDto> _attractionCache = new();
    private const int CatalogPageSize = 50;
    private int _catalogPage = 1;
    private int _catalogRequestVersion;
    private bool _catalogHasMore;
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
    public ObservableCollection<BirthdayContactDto> BirthdayContacts => _birthdayContacts;
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
            await _api.EnsureApiAndDatabaseAsync();
            RetryConnectionButton.Visibility = Visibility.Collapsed;
            ConnectionStatusText.Text = "Облачная база подключена";

            var restoredUser = await _api.TryRestoreSessionAsync();
            if (restoredUser is not null)
            {
                _currentUser = restoredUser;
                ProfileNameText.Text = restoredUser.DisplayName;
                AuthPage.Visibility = Visibility.Collapsed;
                MainShell.Visibility = Visibility.Visible;
                ShowPage("home");
                SetAuthStatus("Сессия восстановлена.", "success");
                await LoadMainDataAsync();
            }
            else
            {
                MainShell.Visibility = Visibility.Collapsed;
                AuthPage.Visibility = Visibility.Visible;
                SetAuthStatus("Облачная база подключена. Можно войти или создать аккаунт.", "success");
            }
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
                if (password.Length is < 12 or > 128)
                {
                    SetAuthStatus("Пароль должен содержать от 12 до 128 символов.", "error");
                    return;
                }

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
        PasswordBox.PlaceholderText = _isRegisterMode ? "От 12 до 128 символов" : "Введите пароль";
        PasswordBox.MaxLength = _isRegisterMode ? 128 : 0;
        SetAuthStatus(string.Empty, "muted");
    }

    private async Task LoadMainDataAsync()
    {
        try
        {
            SearchBox.Text = string.Empty;
            var categoriesTask = _api.GetCategoriesAsync();
            var catalogTask = _api.GetCatalogPageAsync(page: 1, pageSize: CatalogPageSize);
            var visitsTask = _api.GetVisitsAsync();
            var wishlistTask = _api.GetWishlistAsync();
            var summaryTask = _api.GetSummaryAsync();
            await Task.WhenAll(categoriesTask, catalogTask, visitsTask, wishlistTask, summaryTask);

            _categories = await categoriesTask;
            var catalogPage = await catalogTask;
            ApplyCatalogPage(catalogPage);

            _visits.Clear();
            foreach (var item in await visitsTask) _visits.Add(item);

            _wishlistIds.Clear();
            foreach (var id in await wishlistTask) _wishlistIds.Add(id);
            await EnsureAttractionsCachedAsync(_wishlistIds);
            RefreshWishlistCollection();
            PopulateCategories();

            var summary = await summaryTask;
            VisitsCountText.Text = summary.VisitCount.ToString();
            PlacesCountText.Text = summary.DistinctAttractions.ToString();
            CitiesCountText.Text = summary.Cities.ToString();
            CountriesCountText.Text = summary.Countries.ToString();
            ConnectionStatusText.Text = "Облачная база подключена";
            await RefreshLocalRecommendationsAsync();
            await LoadDashboardBirthdayReminderAsync();
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
        foreach (var attraction in _wishlistIds
                     .Select(id => _attractionCache.GetValueOrDefault(id))
                     .Where(attraction => attraction is not null)
                     .Cast<AttractionDto>()
                     .OrderBy(attraction => attraction.Country)
                     .ThenBy(attraction => attraction.City)
                     .ThenBy(attraction => attraction.Name))
        {
            _wishlistAttractions.Add(attraction);
        }
        RemoveWishlistButton.IsEnabled = WishlistList.SelectedItem is AttractionDto;
    }

    private async Task EnsureAttractionsCachedAsync(IEnumerable<int> attractionIds)
    {
        var missingIds = attractionIds
            .Where(id => id > 0 && !_attractionCache.ContainsKey(id))
            .Distinct()
            .ToArray();
        if (missingIds.Length == 0) return;

        var attractions = await _api.GetCatalogByIdsAsync(missingIds);
        foreach (var attraction in attractions)
            _attractionCache[attraction.Id] = attraction;
    }

    private void ApplyCatalogPage(PagedItemsResponse<AttractionDto> page)
    {
        var selectedId = _selectedAttraction?.Id;
        _catalogPage = page.Page;
        _catalogHasMore = page.HasMore;

        _attractions.Clear();
        foreach (var attraction in page.Items)
        {
            _attractionCache[attraction.Id] = attraction;
            _attractions.Add(attraction);
        }

        _selectedAttraction = selectedId.HasValue
            ? page.Items.FirstOrDefault(attraction => attraction.Id == selectedId.Value)
            : null;
        UpdateAttractionDetails(_selectedAttraction);
        CatalogList.SelectedItem = _selectedAttraction;

        CatalogPageStatus.Text = $"Страница {page.Page} · {page.TotalCount:N0} мест";
        CatalogPreviousButton.IsEnabled = page.Page > 1;
        CatalogNextButton.IsEnabled = page.HasMore;
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchCatalogAsync(page: 1);

    private async void CatalogPrevious_Click(object sender, RoutedEventArgs e) =>
        await SearchCatalogAsync(Math.Max(1, _catalogPage - 1));

    private async void CatalogNext_Click(object sender, RoutedEventArgs e) =>
        await SearchCatalogAsync(_catalogPage + 1);

    private async Task SearchCatalogAsync(int page)
    {
        var requestVersion = Interlocked.Increment(ref _catalogRequestVersion);
        CatalogSearchButton.IsEnabled = false;
        CatalogPreviousButton.IsEnabled = false;
        CatalogNextButton.IsEnabled = false;

        try
        {
            int? categoryId = null;
            if (CategoryCombo.SelectedItem is ComboBoxItem { Tag: int id }) categoryId = id;
            var result = await _api.GetCatalogPageAsync(
                SearchBox.Text, categoryId, page, CatalogPageSize);
            if (requestVersion != Volatile.Read(ref _catalogRequestVersion)) return;

            ApplyCatalogPage(result);
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref _catalogRequestVersion))
                await ShowMessageAsync("Ошибка поиска", ex.Message);
        }
        finally
        {
            if (requestVersion == Volatile.Read(ref _catalogRequestVersion))
            {
                CatalogSearchButton.IsEnabled = true;
                CatalogPreviousButton.IsEnabled = _catalogPage > 1;
                CatalogNextButton.IsEnabled = _catalogHasMore;
            }
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

    private async void RefreshRecommendations_Click(object sender, RoutedEventArgs e) =>
        await RefreshLocalRecommendationsAsync();

    private void OpenResidenceFromDashboard_Click(object sender, RoutedEventArgs e) =>
        Profile_Click(sender, e);

    private async Task RefreshLocalRecommendationsAsync()
    {
        if (_currentUser is null) return;

        RefreshRecommendationsButton.IsEnabled = false;
        DashboardRecommendationsStatusText.Text = "Подбираем места по данным профиля…";
        try
        {
            var recommendations = await _api.GetLocalRecommendationsAsync();
            ApplyLocalRecommendations(recommendations);
        }
        catch (Exception ex)
        {
            DashboardRecommendationsList.ItemsSource = Array.Empty<AttractionDto>();
            DashboardRecommendationsStatusText.Text = "Не удалось загрузить локальную подборку: " + ex.Message;
        }
        finally
        {
            RefreshRecommendationsButton.IsEnabled = true;
        }
    }

    private void ApplyLocalRecommendations(IReadOnlyList<AttractionDto> recommendations)
    {
        var country = _currentUser?.ResidenceCountry?.Trim();
        var city = _currentUser?.ResidenceCity?.Trim();
        var hasCountry = !string.IsNullOrWhiteSpace(country);

        DashboardRecommendationsList.ItemsSource = recommendations.ToList();
        DashboardResidenceButton.Content = hasCountry ? "Изменить место" : "Указать место";

        if (!hasCountry)
        {
            DashboardRecommendationsStatusText.Text =
                "Укажи страну и, при желании, город в профиле. Посещённые места не попадут в подборку.";
            return;
        }

        if (recommendations.Count == 0)
        {
            DashboardRecommendationsStatusText.Text =
                $"В каталоге пока нет подходящих непосещённых мест для {city ?? country}.";
            return;
        }

        DashboardRecommendationsStatusText.Text = string.IsNullOrWhiteSpace(city)
            ? $"Подборка по стране «{country}». Посещённые достопримечательности исключены."
            : $"Подборка для «{city}», {country}: сначала места в этом городе, затем другие места страны. Посещённые исключены.";
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
        _attractionCache[attraction.Id] = attraction;
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
        try
        {
            if (!_attractionCache.TryGetValue(_selectedVisit.AttractionId, out var attraction))
            {
                var matches = await _api.GetCatalogByIdsAsync(new[] { _selectedVisit.AttractionId });
                attraction = matches.FirstOrDefault();
                if (attraction is not null)
                    _attractionCache[attraction.Id] = attraction;
            }

            if (attraction is null)
            {
                await ShowMessageAsync("Место не найдено", "Не удалось найти достопримечательность в каталоге.");
                return;
            }

            await ShowVisitDialogAsync(attraction, _selectedVisit);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Не удалось открыть посещение", ex.Message);
        }
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
        if (_attractions.Count == 0) await SearchCatalogAsync(page: 1);
    }
    private async void VisitsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("visits");
        await LoadMainDataAsync();
    }
    private void WishlistNav_Click(object sender, RoutedEventArgs e) => ShowPage("wishlist");

    private async void MapNav_Click(object sender, RoutedEventArgs e) => await OpenMapAsync();

    private void MapWebView_WebResourceRequested(
        object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var requestUri)) return;

        var authorization = _api.GetMapAuthorizationHeader(requestUri);
        if (!string.IsNullOrWhiteSpace(authorization))
            args.Request.Headers.SetHeader("Authorization", authorization);
    }

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
                var coreWebView = MapWebView.CoreWebView2;
                coreWebView.Settings.AreDefaultContextMenusEnabled = false;
                coreWebView.AddWebResourceRequestedFilter(
                    _api.GetMapApiRequestFilter(), CoreWebView2WebResourceContext.All);
                coreWebView.WebResourceRequested += MapWebView_WebResourceRequested;
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
        FriendsPage.Visibility = page == "friends" ? Visibility.Visible : Visibility.Collapsed;

        (PageEyebrow.Text, PageTitle.Text) = page switch
        {
            "home" => ("Личная карта воспоминаний", $"Привет, {_currentUser?.DisplayName ?? "путешественник"}"),
            "catalog" => ("Места для новых историй", "Каталог мест"),
            "visits" => ("Сохранённые моменты", "Мои посещения"),
            "wishlist" => ("Идеи для следующей поездки", "Хочу посетить"),
            "map" => ("Исследуй мир", "Карта открытий"),
            "profile" => ("Личные данные и оформление", "Профиль и настройки"),
            "friends" => ("Общение и совместные открытия", "Друзья"),
            "support" => ("Контакты и полезные ссылки", "Поддержка"),
            _ => ("Исследуй мир", "Карта открытий")
        };
        HomeNavButton.Background = page == "home" ? Brush("#29443D") : Brush("Transparent");
        SupportNavButton.Background = page == "support" ? Brush("#29443D") : Brush("Transparent");
        FriendsNavButton.Background = page == "friends" ? Brush("#29443D") : Brush("Transparent");
    }


    private async void FriendsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("friends");
        await LoadFriendsPageAsync();
    }

    private async void FriendsRefresh_Click(object sender, RoutedEventArgs e) =>
        await LoadFriendsPageAsync();

    private async void FriendsSearch_Click(object sender, RoutedEventArgs e) =>
        await SearchFriendsAsync();

    private async void FriendsSearchBox_KeyDown(
        object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await SearchFriendsAsync();
    }

    private async Task LoadFriendsPageAsync()
    {
        if (_currentUser is null) return;

        FriendsRefreshButton.IsEnabled = false;
        FriendsListStatusText.Text = "Загружаем список друзей…";
        FriendsListPanel.Children.Clear();

        try
        {
            var friends = await _api.GetFriendsAsync();
            FriendsCountText.Text = friends.Count.ToString();

            if (friends.Count == 0)
            {
                FriendsListStatusText.Text = "Пока здесь пусто. Найди пользователя по логину выше, чтобы отправить заявку.";
                return;
            }

            FriendsListStatusText.Text = $"В списке {friends.Count} {GetRussianCountWord(friends.Count, "друг", "друга", "друзей")}.";
            foreach (var friend in friends)
            {
                var actions = new UIElement[]
                {
                    CreateSocialButton("Профиль", friend.UserId, friend.Login, OpenFriendProfile_Click),
                    CreateSocialButton("Удалить", friend.UserId, friend.Login, RemoveFriend_Click)
                };
                var card = CreateSocialCard(friend.UserId, friend.Login, friend.DisplayName, friend.HasAvatar, actions);
                FriendsListPanel.Children.Add(card);
            }
        }
        catch (Exception ex)
        {
            FriendsListStatusText.Text = "Не удалось загрузить список друзей: " + ex.Message;
        }
        finally
        {
            FriendsRefreshButton.IsEnabled = true;
        }
    }

    private async Task SearchFriendsAsync()
    {
        var loginPrefix = FriendsSearchBox.Text.Trim();
        FriendsSearchResultsPanel.Children.Clear();

        if (loginPrefix.Length < 3)
        {
            FriendsSearchStatusText.Text = "Для поиска введи не менее трёх символов.";
            return;
        }

        if (loginPrefix.Length > 50)
        {
            FriendsSearchStatusText.Text = "Логин для поиска не должен быть длиннее 50 символов.";
            return;
        }

        FriendsSearchButton.IsEnabled = false;
        FriendsSearchStatusText.Text = "Ищем пользователей…";

        try
        {
            var users = await _api.SearchUsersAsync(loginPrefix);
            if (users.Count == 0)
            {
                FriendsSearchStatusText.Text = "Пользователи с таким началом логина не найдены.";
                return;
            }

            FriendsSearchStatusText.Text = $"Найдено пользователей: {users.Count}.";
            foreach (var user in users)
            {
                var actions = new List<UIElement>
                {
                    CreateSocialButton("Профиль", user.UserId, user.Login, OpenFriendProfile_Click)
                };

                switch (user.RelationshipStatus)
                {
                    case "none":
                        actions.Add(CreateSocialButton("Добавить", user.UserId, user.Login, SendFriendRequest_Click, primary: true));
                        break;
                    case "outgoing_pending":
                        actions.Add(CreateSocialButton("Заявка отправлена", user.UserId, user.Login, null, enabled: false));
                        break;
                    case "incoming_pending":
                        actions.Add(CreateSocialButton("Входящая заявка", user.UserId, user.Login, null, enabled: false));
                        break;
                    case "friends":
                        actions.Add(CreateSocialButton("Уже друзья", user.UserId, user.Login, null, enabled: false));
                        break;
                    default:
                        actions.Add(CreateSocialButton("Недоступно", user.UserId, user.Login, null, enabled: false));
                        break;
                }

                FriendsSearchResultsPanel.Children.Add(
                    CreateSocialCard(user.UserId, user.Login, user.DisplayName, user.HasAvatar, actions));
            }
        }
        catch (Exception ex)
        {
            FriendsSearchStatusText.Text = "Не удалось выполнить поиск: " + ex.Message;
        }
        finally
        {
            FriendsSearchButton.IsEnabled = true;
        }
    }

    private Border CreateSocialCard(
        int userId, string login, string? displayName, bool? hasAvatar, IEnumerable<UIElement> actions)
    {
        var card = new Border
        {
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#E7EBE5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(13)
        };

        var layout = new Grid { ColumnSpacing = 13 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = CreateSocialAvatar(userId, login, displayName, hasAvatar);
        Grid.SetColumn(avatar, 0);
        layout.Children.Add(avatar);

        var identity = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        identity.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(displayName) ? login : displayName,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("#1B2A25"),
            TextWrapping = TextWrapping.Wrap
        });
        identity.Children.Add(new TextBlock
        {
            Text = "@" + login,
            FontSize = 12,
            Foreground = Brush("#819087")
        });
        Grid.SetColumn(identity, 1);
        layout.Children.Add(identity);

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var action in actions)
            actionPanel.Children.Add(action);
        Grid.SetColumn(actionPanel, 2);
        layout.Children.Add(actionPanel);

        card.Child = layout;
        return card;
    }

    private Grid CreateSocialAvatar(int userId, string login, string? displayName, bool? hasAvatar)
    {
        var avatar = new Ellipse
        {
            Width = 52,
            Height = 52,
            Fill = Brush("#DDEBCB")
        };
        var fallback = new TextBlock
        {
            Text = GetInitials(displayName, login),
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("#5F7844"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var container = new Grid { Width = 52, Height = 52 };
        container.Children.Add(avatar);
        container.Children.Add(fallback);

        if (hasAvatar == true)
            _ = LoadSocialAvatarAsync(userId, avatar, fallback);

        return container;
    }

    private async Task LoadSocialAvatarAsync(int userId, Ellipse avatar, TextBlock fallback)
    {
        try
        {
            var bytes = await _api.GetUserAvatarBytesAsync(userId, thumbnail: true);
            if (bytes is null || bytes.Length == 0) return;

            using var imageStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(imageStream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }

            imageStream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(imageStream);
            avatar.Fill = new ImageBrush
            {
                ImageSource = bitmap,
                Stretch = Stretch.UniformToFill
            };
            fallback.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Keep the initials fallback when an avatar is unavailable or cannot be decoded.
        }
    }

    private static string GetInitials(string? displayName, string login)
    {
        var value = string.IsNullOrWhiteSpace(displayName) ? login.Trim() : displayName.Trim();
        if (value.Length == 0) return "??";
        return value.Length == 1
            ? value.ToUpperInvariant()
            : string.Concat(char.ToUpperInvariant(value[0]), char.ToUpperInvariant(value[1]));
    }

    private Button CreateSocialButton(
        string caption, int userId, string login, RoutedEventHandler? handler,
        bool primary = false, bool enabled = true)
    {
        var button = new Button
        {
            Content = caption,
            Tag = (userId, login),
            IsEnabled = enabled,
            Style = (Style)Root.Resources[primary ? "PrimaryButtonStyle" : "QuietButtonStyle"]
        };
        if (handler is not null)
            button.Click += handler;
        return button;
    }

    private static bool TryGetSocialUser(object sender, out int userId, out string login)
    {
        if (sender is Button button && button.Tag is ValueTuple<int, string> value)
        {
            userId = value.Item1;
            login = value.Item2;
            return userId > 0;
        }

        userId = 0;
        login = string.Empty;
        return false;
    }

    private async void SendFriendRequest_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSocialUser(sender, out var userId, out var login)) return;
        try
        {
            await _api.SendFriendRequestAsync(userId);
            FriendsSearchStatusText.Text = $"Заявка для @{login} отправлена.";
            await LoadFriendsPageAsync();
            await SearchFriendsAsync();
        }
        catch (Exception ex)
        {
            FriendsSearchStatusText.Text = "Не удалось отправить заявку: " + ex.Message;
        }
    }

    private async void RemoveFriend_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSocialUser(sender, out var userId, out var login)) return;
        if (await ConfirmAsync("Удалить из друзей?", $"Пользователь @{login} будет удалён из списка друзей.") != ContentDialogResult.Primary)
            return;

        try
        {
            await _api.RemoveFriendAsync(userId);
            FriendsListStatusText.Text = $"Пользователь @{login} удалён из друзей.";
            await LoadFriendsPageAsync();
            if (FriendsSearchBox.Text.Trim().Length >= 3)
                await SearchFriendsAsync();
        }
        catch (Exception ex)
        {
            FriendsListStatusText.Text = "Не удалось удалить друга: " + ex.Message;
        }
    }

    private async void OpenFriendProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSocialUser(sender, out var userId, out var login)) return;
        try
        {
            var profile = await _api.GetUserProfileAsync(userId);
            var content = new StackPanel { Spacing = 12, MinWidth = 300 };
            content.Children.Add(CreateSocialAvatar(userId, login, profile.DisplayName, profile.HasAvatar));
            content.Children.Add(new TextBlock
            {
                Text = "@" + login,
                FontSize = 12,
                Foreground = Brush("#819087")
            });

            if (!string.IsNullOrWhiteSpace(profile.ResidenceCity))
                content.Children.Add(new TextBlock
                {
                    Text = "Город: " + profile.ResidenceCity,
                    FontSize = 13,
                    Foreground = Brush("#53645B"),
                    TextWrapping = TextWrapping.Wrap
                });

            if (!string.IsNullOrWhiteSpace(profile.ResidenceCountry))
                content.Children.Add(new TextBlock
                {
                    Text = "Страна: " + profile.ResidenceCountry,
                    FontSize = 13,
                    Foreground = Brush("#53645B"),
                    TextWrapping = TextWrapping.Wrap
                });

            var dialog = new ContentDialog
            {
                Title = profile.DisplayName ?? login,
                Content = content,
                CloseButtonText = "Закрыть",
                RequestedTheme = Root.RequestedTheme,
                XamlRoot = Root.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Не удалось открыть профиль", ex.Message);
        }
    }

    private static string GetRussianCountWord(int count, string one, string few, string many)
    {
        var remainder100 = count % 100;
        if (remainder100 is >= 11 and <= 14) return many;
        return count % 10 switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    private void SupportNav_Click(object sender, RoutedEventArgs e) => ShowPage("support");

    private async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null) return;

        ProfileDisplayNameBox.Text = _currentUser.DisplayName;
        ProfileLoginText.Text = _currentUser.Login;
        ProfileResidenceCountryBox.Text = _currentUser.ResidenceCountry ?? string.Empty;
        ProfileResidenceCityBox.Text = _currentUser.ResidenceCity ?? string.Empty;
        ProfileThemeToggle.IsOn = _isDarkTheme;
        SetProfileStatus(string.Empty, "muted");
        SetResidenceStatus(string.Empty, "muted");
        ShowPage("profile");
        await LoadProfileAvatarAsync();
        await LoadProfilePrivacyAsync();
        await LoadBirthdaySettingsAsync();
    }

    private async Task LoadProfilePrivacyAsync()
    {
        if (_currentUser is null) return;

        ProfilePrivacySaveButton.IsEnabled = false;
        try
        {
            var settings = await _api.GetProfilePrivacyAsync();
            ProfileDisplayNameVisibilityBox.SelectedValue = settings.DisplayNameVisibility;
            ProfileAvatarVisibilityBox.SelectedValue = settings.AvatarVisibility;
            ProfileResidenceCountryVisibilityBox.SelectedValue = settings.ResidenceCountryVisibility;
            ProfileResidenceCityVisibilityBox.SelectedValue = settings.ResidenceCityVisibility;
            SetProfilePrivacyStatus(string.Empty, "muted");
        }
        catch (Exception ex)
        {
            SetProfilePrivacyStatus("Не удалось загрузить настройки видимости: " + ex.Message, "error");
        }
        finally
        {
            ProfilePrivacySaveButton.IsEnabled = true;
        }
    }

    private static string? SelectedVisibility(ComboBox comboBox) =>
        comboBox.SelectedValue?.ToString();

    private void SetProfilePrivacyStatus(string message, string tone)
    {
        ProfilePrivacyStatusText.Text = message;
        ProfilePrivacyStatusText.Foreground = Brush(tone switch
        {
            "success" => "#4F8758",
            "error" => "#B85D4D",
            _ => "#7A8980"
        });
    }

    private async Task LoadProfileAvatarAsync()
    {
        if (_currentUser is null) return;

        ProfileAvatarInitialsText.Text = string.IsNullOrWhiteSpace(_currentUser.DisplayName)
            ? "LL"
            : _currentUser.DisplayName.Trim()[0].ToString().ToUpperInvariant();
        ProfileAvatarInitialsText.Visibility = Visibility.Visible;
        ProfileAvatarImageEllipse.Visibility = Visibility.Collapsed;
        ProfileAvatarDeleteButton.IsEnabled = _currentUser.HasAvatar;

        if (!_currentUser.HasAvatar) return;

        try
        {
            var bytes = await _api.GetAvatarBytesAsync(thumbnail: true);
            if (bytes is null)
            {
                _currentUser = _currentUser with { HasAvatar = false };
                ProfileAvatarDeleteButton.IsEnabled = false;
                return;
            }

            using var imageStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(imageStream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }

            imageStream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(imageStream);
            ProfileAvatarImageEllipse.Fill = new ImageBrush
            {
                ImageSource = bitmap,
                Stretch = Stretch.UniformToFill
            };
            ProfileAvatarImageEllipse.Visibility = Visibility.Visible;
            ProfileAvatarInitialsText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            SetAvatarStatus("Не удалось загрузить аватар: " + ex.Message, "error");
        }
    }

    private async void ChangeAvatar_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null) return;

        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" })
            picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        ProfileAvatarChangeButton.IsEnabled = false;
        ProfileAvatarDeleteButton.IsEnabled = false;
        SetAvatarStatus("Загружаем и обрабатываем изображение…", "muted");
        try
        {
            await _api.UploadAvatarAsync(file);
            _currentUser = _currentUser with { HasAvatar = true };
            await LoadProfileAvatarAsync();
            SetAvatarStatus("Аватар загружен. Миниатюра создана сервером.", "success");
        }
        catch (Exception ex)
        {
            SetAvatarStatus(ex.Message, "error");
            ProfileAvatarDeleteButton.IsEnabled = _currentUser.HasAvatar;
        }
        finally
        {
            ProfileAvatarChangeButton.IsEnabled = true;
        }
    }

    private async void DeleteAvatar_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null || !_currentUser.HasAvatar) return;

        var confirmation = await ConfirmAsync("Удалить аватар?", "Фотография профиля и её миниатюра будут удалены.");
        if (confirmation != ContentDialogResult.Primary) return;

        ProfileAvatarChangeButton.IsEnabled = false;
        ProfileAvatarDeleteButton.IsEnabled = false;
        SetAvatarStatus("Удаляем аватар…", "muted");
        try
        {
            await _api.DeleteAvatarAsync();
            _currentUser = _currentUser with { HasAvatar = false };
            await LoadProfileAvatarAsync();
            SetAvatarStatus("Аватар удалён.", "success");
        }
        catch (Exception ex)
        {
            SetAvatarStatus(ex.Message, "error");
            ProfileAvatarDeleteButton.IsEnabled = _currentUser.HasAvatar;
        }
        finally
        {
            ProfileAvatarChangeButton.IsEnabled = true;
        }
    }

    private void SetAvatarStatus(string message, string tone)
    {
        ProfileAvatarStatusText.Text = message;
        ProfileAvatarStatusText.Foreground = Brush(tone switch
        {
            "success" => "#4F8758",
            "error" => "#B85D4D",
            _ => "#7A8980"
        });
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

    private async void SaveProfilePrivacy_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null) return;

        var displayName = SelectedVisibility(ProfileDisplayNameVisibilityBox);
        var avatar = SelectedVisibility(ProfileAvatarVisibilityBox);
        var country = SelectedVisibility(ProfileResidenceCountryVisibilityBox);
        var city = SelectedVisibility(ProfileResidenceCityVisibilityBox);
        var allowed = new[] { "private", "friends", "registered" };
        if (new[] { displayName, avatar, country, city }.Any(value => value is null || !allowed.Contains(value)))
        {
            SetProfilePrivacyStatus("Выбери видимость для каждого поля.", "error");
            return;
        }

        ProfilePrivacySaveButton.IsEnabled = false;
        SetProfilePrivacyStatus("Сохраняем настройки видимости…", "muted");
        try
        {
            var settings = new UpdateProfilePrivacyRequest(
                displayName!, avatar!, country!, city!);
            await _api.UpdateProfilePrivacyAsync(settings);
            SetProfilePrivacyStatus("Настройки видимости сохранены.", "success");
        }
        catch (Exception ex)
        {
            SetProfilePrivacyStatus(ex.Message, "error");
        }
        finally
        {
            ProfilePrivacySaveButton.IsEnabled = true;
        }
    }

    private async void SaveResidence_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null) return;

        var country = string.IsNullOrWhiteSpace(ProfileResidenceCountryBox.Text) ? null : ProfileResidenceCountryBox.Text.Trim();
        var city = string.IsNullOrWhiteSpace(ProfileResidenceCityBox.Text) ? null : ProfileResidenceCityBox.Text.Trim();

        if (country is { Length: > 100 } || city is { Length: > 100 })
        {
            SetResidenceStatus("Страна и город должны содержать не более 100 символов.", "error");
            return;
        }
        if (city is not null && country is null)
        {
            SetResidenceStatus("Укажи страну, чтобы сохранить город проживания.", "error");
            return;
        }
        if (string.Equals(country, _currentUser.ResidenceCountry, StringComparison.Ordinal) &&
            string.Equals(city, _currentUser.ResidenceCity, StringComparison.Ordinal))
        {
            SetResidenceStatus("Изменений нет.", "muted");
            return;
        }

        ProfileResidenceSaveButton.IsEnabled = false;
        SetResidenceStatus("Сохраняем место проживания…", "muted");
        try
        {
            var updatedUser = await _api.UpdateResidenceAsync(country, city);
            _currentUser = updatedUser;
            ProfileNameText.Text = updatedUser.DisplayName;
            ProfileLoginText.Text = updatedUser.Login;
            ProfileResidenceCountryBox.Text = updatedUser.ResidenceCountry ?? string.Empty;
            ProfileResidenceCityBox.Text = updatedUser.ResidenceCity ?? string.Empty;
            SetResidenceStatus("Место проживания сохранено. История посещений не изменена.", "success");
            await RefreshLocalRecommendationsAsync();
        }
        catch (Exception ex)
        {
            SetResidenceStatus(ex.Message, "error");
        }
        finally
        {
            ProfileResidenceSaveButton.IsEnabled = true;
        }
    }

    private void SetResidenceStatus(string message, string tone)
    {
        ProfileResidenceStatusText.Text = message;
        ProfileResidenceStatusText.Foreground = Brush(tone switch
        {
            "success" => "#4F8758",
            "error" => "#B85D4D",
            _ => "#7A8980"
        });
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

    private static DateTimeOffset ToCalendarDate(DateOnly value)
    {
        var localDate = new DateTime(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
    }

    private void InitializeBirthdayDatePickers()
    {
        var minimum = ToCalendarDate(new DateOnly(1900, 1, 1));
        var maximum = ToCalendarDate(DateOnly.FromDateTime(DateTime.Today));
        ProfileBirthDatePicker.MinDate = minimum;
        ProfileBirthDatePicker.MaxDate = maximum;
        BirthdayContactDatePicker.MinDate = minimum;
        BirthdayContactDatePicker.MaxDate = maximum;
    }

    private async Task LoadBirthdaySettingsAsync()
    {
        InitializeBirthdayDatePickers();
        SetBirthDateStatus(string.Empty, "muted");
        SetBirthdayContactsStatus(string.Empty, "muted");
        ResetBirthdayContactEditor();

        try
        {
            var birthDate = await _api.GetMyBirthDateAsync();
            ProfileBirthDatePicker.Date = birthDate is { } date ? ToCalendarDate(date) : null;
            await LoadBirthdayContactsAsync();
        }
        catch (Exception ex)
        {
            SetBirthDateStatus("Не удалось загрузить дату рождения: " + ex.Message, "error");
            SetBirthdayContactsStatus("Не удалось загрузить дни рождения друзей: " + ex.Message, "error");
        }
    }

    private async Task LoadBirthdayContactsAsync()
    {
        var contacts = await _api.GetBirthdayContactsAsync(DateOnly.FromDateTime(DateTime.Today));
        _birthdayContacts.Clear();
        foreach (var contact in contacts)
            _birthdayContacts.Add(contact);

        UpdateDashboardBirthdayReminder(contacts);
    }

    private async Task LoadDashboardBirthdayReminderAsync()
    {
        try
        {
            var contacts = await _api.GetBirthdayContactsAsync(DateOnly.FromDateTime(DateTime.Today));
            UpdateDashboardBirthdayReminder(contacts);
        }
        catch (Exception ex)
        {
            DashboardBirthdayReminderText.Text = "Не удалось загрузить напоминания о днях рождения: " + ex.Message;
        }
    }

    private void UpdateDashboardBirthdayReminder(IReadOnlyList<BirthdayContactDto> contacts)
    {
        if (contacts.Count == 0)
        {
            DashboardBirthdayReminderText.Text = "Добавь дни рождения друзей в профиле — ближайшие напоминания появятся здесь.";
            return;
        }

        var next = contacts[0];
        var when = next.DaysUntilBirthday switch
        {
            0 => "Сегодня",
            1 => "Завтра",
            _ => $"Через {next.DaysUntilBirthday} дн."
        };
        DashboardBirthdayReminderText.Text = $"{when}: день рождения — {next.DisplayName}. {next.BirthdayDetails}";
    }

    private async void SaveBirthDate_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBirthDatePicker.Date is not { } selectedDate)
        {
            SetBirthDateStatus("Выбери дату рождения или нажми «Очистить», чтобы удалить сохранённую дату.", "error");
            return;
        }

        var date = DateOnly.FromDateTime(selectedDate.DateTime);
        if (date > DateOnly.FromDateTime(DateTime.Today))
        {
            SetBirthDateStatus("Дата рождения не может быть в будущем.", "error");
            return;
        }

        ProfileBirthDateSaveButton.IsEnabled = false;
        ProfileBirthDateClearButton.IsEnabled = false;
        SetBirthDateStatus("Сохраняем приватную дату рождения…", "muted");
        try
        {
            await _api.UpdateMyBirthDateAsync(date);
            SetBirthDateStatus("Дата рождения сохранена. Она не отображается в публичном профиле.", "success");
            await RefreshLocalRecommendationsAsync();
        }
        catch (Exception ex)
        {
            SetBirthDateStatus(ex.Message, "error");
        }
        finally
        {
            ProfileBirthDateSaveButton.IsEnabled = true;
            ProfileBirthDateClearButton.IsEnabled = true;
        }
    }

    private async void ClearBirthDate_Click(object sender, RoutedEventArgs e)
    {
        ProfileBirthDateSaveButton.IsEnabled = false;
        ProfileBirthDateClearButton.IsEnabled = false;
        try
        {
            await _api.UpdateMyBirthDateAsync(null);
            ProfileBirthDatePicker.Date = null;
            SetBirthDateStatus("Дата рождения удалена. Возрастные ограничения для рекомендаций без неё будут применяться строго.", "success");
            await RefreshLocalRecommendationsAsync();
        }
        catch (Exception ex)
        {
            SetBirthDateStatus(ex.Message, "error");
        }
        finally
        {
            ProfileBirthDateSaveButton.IsEnabled = true;
            ProfileBirthDateClearButton.IsEnabled = true;
        }
    }

    private async void SaveBirthdayContact_Click(object sender, RoutedEventArgs e)
    {
        var displayName = BirthdayContactNameBox.Text.Trim();
        if (displayName.Length is < 1 or > 80)
        {
            SetBirthdayContactsStatus("Имя должно содержать от 1 до 80 символов.", "error");
            return;
        }
        if (BirthdayContactDatePicker.Date is not { } selectedDate)
        {
            SetBirthdayContactsStatus("Выбери дату рождения контакта.", "error");
            return;
        }

        var birthDate = DateOnly.FromDateTime(selectedDate.DateTime);
        if (birthDate > DateOnly.FromDateTime(DateTime.Today))
        {
            SetBirthdayContactsStatus("Дата рождения не может быть в будущем.", "error");
            return;
        }

        BirthdayContactSaveButton.IsEnabled = false;
        BirthdayContactCancelEditButton.IsEnabled = false;
        SetBirthdayContactsStatus(_editingBirthdayContactId.HasValue ? "Сохраняем изменения…" : "Добавляем контакт…", "muted");
        try
        {
            if (_editingBirthdayContactId is { } contactId)
                await _api.UpdateBirthdayContactAsync(contactId, displayName, birthDate);
            else
                await _api.CreateBirthdayContactAsync(displayName, birthDate);

            ResetBirthdayContactEditor();
            await LoadBirthdayContactsAsync();
            SetBirthdayContactsStatus("День рождения сохранён. Напоминание и ежегодное событие доступны в списке.", "success");
        }
        catch (Exception ex)
        {
            SetBirthdayContactsStatus(ex.Message, "error");
        }
        finally
        {
            BirthdayContactSaveButton.IsEnabled = true;
            BirthdayContactCancelEditButton.IsEnabled = true;
        }
    }

    private void EditBirthdayContact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(Convert.ToString(button.Tag), out var contactId))
            return;

        var contact = _birthdayContacts.FirstOrDefault(item => item.Id == contactId);
        if (contact is null) return;

        _editingBirthdayContactId = contact.Id;
        BirthdayContactNameBox.Text = contact.DisplayName;
        BirthdayContactDatePicker.Date = ToCalendarDate(contact.BirthDate);
        BirthdayContactSaveButton.Content = "Сохранить";
        BirthdayContactCancelEditButton.Visibility = Visibility.Visible;
        SetBirthdayContactsStatus("Изменяешь контакт: " + contact.DisplayName, "muted");
    }

    private void CancelBirthdayContactEdit_Click(object sender, RoutedEventArgs e)
    {
        ResetBirthdayContactEditor();
        SetBirthdayContactsStatus("Редактирование отменено.", "muted");
    }

    private void ResetBirthdayContactEditor()
    {
        _editingBirthdayContactId = null;
        BirthdayContactNameBox.Text = string.Empty;
        BirthdayContactDatePicker.Date = null;
        BirthdayContactSaveButton.Content = "Добавить";
        BirthdayContactCancelEditButton.Visibility = Visibility.Collapsed;
    }

    private async void DeleteBirthdayContact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(Convert.ToString(button.Tag), out var contactId))
            return;

        button.IsEnabled = false;
        try
        {
            await _api.DeleteBirthdayContactAsync(contactId);
            if (_editingBirthdayContactId == contactId)
                ResetBirthdayContactEditor();
            await LoadBirthdayContactsAsync();
            SetBirthdayContactsStatus("День рождения удалён из твоего списка.", "success");
        }
        catch (Exception ex)
        {
            SetBirthdayContactsStatus(ex.Message, "error");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void ExportBirthdayCalendar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(Convert.ToString(button.Tag), out var contactId))
            return;

        button.IsEnabled = false;
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"birthday-{contactId}"
            };
            picker.FileTypeChoices.Add("Календарь iCalendar", new List<string> { ".ics" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var calendar = await _api.GetBirthdayCalendarAsync(contactId);
            await FileIO.WriteTextAsync(file, calendar, UnicodeEncoding.Utf8);
            SetBirthdayContactsStatus("Файл календаря сохранён. Импортируй .ics в календарь, чтобы получать ежегодные напоминания.", "success");
        }
        catch (Exception ex)
        {
            SetBirthdayContactsStatus(ex.Message, "error");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void SetBirthDateStatus(string message, string tone)
    {
        ProfileBirthDateStatusText.Text = message;
        ProfileBirthDateStatusText.Foreground = Brush(tone switch
        {
            "success" => "#4F8758",
            "error" => "#B85D4D",
            _ => "#7A8980"
        });
    }

    private void SetBirthdayContactsStatus(string message, string tone)
    {
        BirthdayContactsStatusText.Text = message;
        BirthdayContactsStatusText.Foreground = Brush(tone switch
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
            await ShowMessageAsync(
                "Не удалось завершить сессию",
                "Сервер не подтвердил выход. Ты останешься в аккаунте, пока выход не будет подтверждён. Проверь подключение и повтори попытку.\n\n" + ex.Message);
            return;
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
