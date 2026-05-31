namespace AIAgentLocal.Views;

public partial class LanguagePickerPage : ContentPage
{
    private readonly List<KeyValuePair<string, string>> _languages;
    public string? SelectedCode { get; private set; }

    public LanguagePickerPage(Dictionary<string, string> languages, string currentCode)
    {
        InitializeComponent();
        _languages = languages.ToList();

        foreach (var kv in _languages)
            LanguagePicker.Items.Add(kv.Value);

        var currentIndex = _languages.FindIndex(kv => kv.Key == currentCode);
        if (currentIndex >= 0)
            LanguagePicker.SelectedIndex = currentIndex;
    }

    private async void OnOkClicked(object? sender, EventArgs e)
    {
        if (LanguagePicker.SelectedIndex >= 0)
            SelectedCode = _languages[LanguagePicker.SelectedIndex].Key;
        await Navigation.PopModalAsync();
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }
}
