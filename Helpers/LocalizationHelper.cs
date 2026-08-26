using Windows.ApplicationModel.Resources;

namespace SftpExplorerWinUI.Helpers
{
    public static class LocalizationHelper
    {
        private static ResourceLoader? _resourceLoader;

        public static ResourceLoader ResourceLoader
        {
            get
            {
                _resourceLoader ??= new ResourceLoader();
                return _resourceLoader;
            }
        }

        public static string GetString(string key)
        {
            try
            {
                return ResourceLoader.GetString(key);
            }
            catch
            {
                // Не удалось найти локализованную строку - возвращаем ключ
                return key;
            }
        }
    }
}
