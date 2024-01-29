namespace OpenSmc.DomainDesigner.ExcelParser
{
    public static class DocumentHelperExtensions
    {
        public static string GetExtensionByFileName(this string fileName)
        {
            var extension = Path.GetExtension(fileName)?.ToLower();
            if (string.IsNullOrEmpty(extension))
                throw new InvalidOperationException("Missing extension of importing file");
            return extension;
        }

        //TODO: unite with HelperExtensions after Export API will be in develop
        public static bool CellInAlphabeticalOrder(char previousCell, char currentCell)
        {
            return currentCell == 'A' || Math.Abs(previousCell - currentCell) == 1;
        }
    }
}
