using System.ComponentModel;

namespace FileRenamer.Models;

public class RenameItem : INotifyPropertyChanged
{
    public string FullPath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string Extension { get; set; } = "";
    public string ExtractedText { get; set; } = "";
    public int PagesRead { get; set; } = 0;
    public bool WasOcr { get; set; } = false;

    private string _suggestedName = "";
    public string SuggestedName
    {
        get => _suggestedName;
        set { _suggestedName = value; OnPropertyChanged(nameof(SuggestedName)); }
    }

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    private string _status = "Pending";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    private string _category = "Other";
    public string Category
    {
        get => _category;
        set { _category = value; OnPropertyChanged(nameof(Category)); }
    }

    private string _targetSubfolder = "";
    public string TargetSubfolder
    {
        get => _targetSubfolder;
        set 
        { 
            _targetSubfolder = value; 
            OnPropertyChanged(nameof(TargetSubfolder)); 
            OnPropertyChanged(nameof(DestinationDisplay));
        }
    }

    public string DestinationDisplay =>
        string.IsNullOrEmpty(TargetSubfolder)
            ? "Pending shelving"
            : $"Library\\{TargetSubfolder}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
