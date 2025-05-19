using CommunityToolkit.Mvvm.Input;
using SWF = Microsoft.Win32;
using TextureCoordsCalculatorGUI.Services;
using SereniaBLPLib;
using System.IO;
using System.Windows;
using TextureCoordsCalculatorGUI.Misc;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Drawing;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using TextureCoordsCalculatorGUI.ViewModels.Base;
using TextureCoordsCalculatorGUI.Views;
using TextureCoordsCalculatorGUI.Shared;
using System.Globalization;
using System.Collections.Immutable;
using System.Diagnostics;

namespace TextureCoordsCalculatorGUI.ViewModels
{
    public partial class MainViewModel(WagoService wagoService) : BaseViewModel("Texture Coordinates Calculator")
    {
        private readonly WagoService _wagoService = wagoService;
 
        private BlpFile? _blpFile;

        private string? _pngFilePath;

        private Coordinates? _coordinates;

        [ObservableProperty]
        string? normalizedCoords;

        [ObservableProperty]
        BitmapImage? blpImage;

        [ObservableProperty]
        CroppedBitmap? croppedImage;

        [ObservableProperty]
        int imageWidth;

        [ObservableProperty]
        int imageHeight;

        [ObservableProperty]
        int croppedImageWidth;

        [ObservableProperty]
        int croppedImageHeight;

        [ObservableProperty]
        string? croppedAreaLabel;

        [ObservableProperty]
        string? newCoords;

        /// <summary>
        /// Open a .blp file, locally or directly through Wago API.
        /// </summary>
        /// <param name="onlineMode">File will be opened through Wago API.</param>

        [RelayCommand]
        public async Task OpenImageFile(bool onlineMode)
        {
            try
            {
                if (!onlineMode)
                {
                    var fileDialog = new SWF.OpenFileDialog
                    {
                        DefaultExt = ".blp",
                        Filter = "BLP or PNG Files (*.blp;*.png)|*.blp;*.png|BLP Files (*.blp)|*.blp|PNG Files (*.png)|*.png"
                    };

                    var result = fileDialog.ShowDialog();

                    if (result.HasValue && result.Value)
                    {
                        var ext = Path.GetExtension(fileDialog.FileName).ToLowerInvariant();
                        if (ext == ".blp")
                        {
                            _blpFile = new BlpFile(File.OpenRead(fileDialog.FileName));
                            _pngFilePath = null;
                        }
                        else if (ext == ".png")
                        {
                            _blpFile = null;
                            _pngFilePath = fileDialog.FileName;
                        }
                        else
                        {
                            MessageBox.Show("Unsupported file type.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                }
                else
                {
                    InputDialog inputDialog = new();
                    var result = inputDialog.ShowDialog();

                    if (result.HasValue && result.Value)
                    {
                        var stream = await _wagoService.GetCascFile((uint)inputDialog.FileDataId);

                        if (stream is not null)
                        {
                            try
                            {
                                _blpFile = new BlpFile(stream);
                                _pngFilePath = null;
                            }
                            catch (Exception)
                            {
                                MessageBox.Show("Invalid file format", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                return;
                            }
                        }
                    }
                }

                if (_blpFile is not null || _pngFilePath != null)
                {
                    ApplyImage();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        public async Task OpenBrowserAsync()
        {

            var browser = new TextureBrowserDialog(_wagoService);

            browser.ShowDialog();
    
            var texture = browser.SelectedTexture;

            if (!string.IsNullOrEmpty(texture))
            {
                var file = await _wagoService.GetCascFile(Listfile.Instance.GetFileDataId(texture));

                if (file is not null)
                {
                    _blpFile = new BlpFile(file);
                    ApplyImage();
                }
            }

        }

        [RelayCommand]
        public void ApplyChanges()
        {
            if (NewCoords is not null)
            {
                var rawCoords = NewCoords.Split(',')
                    .Where(x => float.TryParse(x, NumberStyles.Any, provider: CultureInfo.InvariantCulture, out var _))
                    .Select(x => float.Parse(x, NumberStyles.Any, provider: CultureInfo.InvariantCulture)).ToImmutableArray();


                float leftCoords = rawCoords.ElementAtOrDefault(0);
                float rightCoords = rawCoords.ElementAtOrDefault(1);
                float topCoords = rawCoords.ElementAtOrDefault(2);
                float bottomCoords = rawCoords.ElementAtOrDefault(3);

                Area.Move(leftCoords, rightCoords, topCoords, bottomCoords);
                NormalizedCoords = $"{NormalizeFloat(leftCoords)},{NormalizeFloat(rightCoords)},{NormalizeFloat(topCoords)},{NormalizeFloat(bottomCoords)}";
            }


            if (CroppedImageWidth > 0 && CroppedImageHeight > 0)
                Area.Resize(CroppedImageWidth, CroppedImageHeight);
        }


        [RelayCommand]
        public void CopyAs(string type)
        {
            if (_coordinates is null)
                return;

            string content = string.Empty;

            switch (type)
            {
                case "lua":
                    content = $"({NormalizeFloat(_coordinates.Left)}, {NormalizeFloat(_coordinates.Right)}, {NormalizeFloat(_coordinates.Top)}, {NormalizeFloat(_coordinates.Bottom)})";
                    break;
                case "xml":
                    content = $"<TexCoords left=\"{NormalizeFloat(_coordinates.Left)}\" right=\"{NormalizeFloat(_coordinates.Right)}\" top=\"{NormalizeFloat(_coordinates.Top)}\" bottom=\"{NormalizeFloat(_coordinates.Bottom)}\"/>";
                    break;
            }


            Clipboard.SetText(content);
        }


        public void CalculateCoordinates(int width, int height, Point leftTopPixels, Point bottomRightPixels)
        {
            var calculator = new TexCoordinatesCalculator(width, height, leftTopPixels, bottomRightPixels);
            _coordinates = calculator.TextureCoordinates;

            if (_coordinates is not null)
            {
                NormalizedCoords = $"{NormalizeFloat(_coordinates.Left)},{NormalizeFloat(_coordinates.Right)},{NormalizeFloat(_coordinates.Top)},{NormalizeFloat(_coordinates.Bottom)}";
                CalculateCroppedImage(leftTopPixels, bottomRightPixels);    
            }
        }

        public void UpdateCroppedAreaLabel(int width, int height)
        {
            CroppedAreaLabel = $"Size: {width}x{height}";
        }

        private void CalculateCroppedImage(Point leftTopPixels, Point bottomRightPixels)
        {
            if (BlpImage == null)
                return;

            // Ensure coordinates are in correct order
            int x1 = (int)Math.Round(Math.Min(leftTopPixels.X, bottomRightPixels.X));
            int y1 = (int)Math.Round(Math.Min(leftTopPixels.Y, bottomRightPixels.Y));
            int x2 = (int)Math.Round(Math.Max(leftTopPixels.X, bottomRightPixels.X));
            int y2 = (int)Math.Round(Math.Max(leftTopPixels.Y, bottomRightPixels.Y));

            // Clamp to image bounds
            x1 = Math.Max(0, Math.Min(x1, BlpImage.PixelWidth - 1));
            y1 = Math.Max(0, Math.Min(y1, BlpImage.PixelHeight - 1));
            x2 = Math.Max(0, Math.Min(x2, BlpImage.PixelWidth));
            y2 = Math.Max(0, Math.Min(y2, BlpImage.PixelHeight));

            int width = x2 - x1;
            int height = y2 - y1;

            if (width > 0 && height > 0)
            {
                var crop = new CroppedBitmap(BlpImage, new(x1, y1, width, height));
                CroppedImage = crop;
                CroppedImageHeight = height;
                CroppedImageWidth = width;
            }
        }

        private void ApplyImage(int mipIndex = 0)
        {
            if (_blpFile is null && _pngFilePath == null)
                return;

            try
            {
                if (_blpFile != null)
                {
                    Bitmap? bmp = null;
                    int mipCount = _blpFile.MipMapCount;
                    int maxMipIndex = mipCount - 1;
                    bool mipLoaded = false;
                    Exception? lastMipException = null;

                    // Output debug info for the loaded BLP file
                    string debugInfo = _blpFile.GetDebugInfo();
                    Debug.WriteLine("BLP Debug Info:\n" + debugInfo);

                    // Validate requested mipIndex
                    if (mipIndex >= 0 && mipIndex <= maxMipIndex)
                    {
                        try
                        {
                            bmp = _blpFile.GetBitmap(mipIndex);
                            if (bmp != null && bmp.Width > 0 && bmp.Height > 0)
                            {
                                mipLoaded = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastMipException = ex;
                            Debug.WriteLine($"Failed to decode requested mipmap {mipIndex}: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"Requested mipmap {mipIndex} is out of range. Valid range: 0 to {maxMipIndex}.");
                    }

                    // Fallback: try loading mipmap 0 if requested index failed
                    if (!mipLoaded)
                    {
                        try
                        {
                            bmp = _blpFile.GetBitmap(0);
                            if (bmp != null && bmp.Width > 0 && bmp.Height > 0)
                            {
                                mipLoaded = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastMipException = ex;
                            Debug.WriteLine($"Fallback: Failed to decode mipmap 0: {ex.Message}\n{ex.StackTrace}");
                        }
                    }

                    if (!mipLoaded || bmp == null)
                    {
                        string errorMsg = $"Failed to decode mipmap {mipIndex} (and fallback to 0) from BLP file. All attempts resulted in errors.";
                        if (lastMipException != null)
                            errorMsg += $"\nLast error: {lastMipException.Message}\n{lastMipException.StackTrace}";
                        MessageBox.Show(errorMsg + "\n\nBLP Debug Info:\n" + debugInfo, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    BlpImage = Utilities.BitMapToImg(bmp);
                    ImageWidth = BlpImage.PixelWidth;
                    ImageHeight = BlpImage.PixelHeight;
                }
                else if (_pngFilePath != null)
                {
                    var bitmap = new BitmapImage();
                    using (var stream = File.OpenRead(_pngFilePath))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }
                    BlpImage = bitmap;
                    ImageWidth = bitmap.PixelWidth;
                    ImageHeight = bitmap.PixelHeight;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load image: {ex.Message}\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string NormalizeFloat(float value)
                => value.ToString().Replace(',', '.');


    }
}
