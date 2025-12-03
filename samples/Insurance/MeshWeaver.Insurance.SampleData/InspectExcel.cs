using ClosedXML.Excel;

namespace MeshWeaver.Insurance.SampleData;

public class ExcelInspector
{
    public static void InspectFile(string filePath)
    {
        using var workbook = new XLWorkbook(filePath, new LoadOptions { RecalculateAllFormulas = false });
        var worksheet = workbook.Worksheets.First();

        Console.WriteLine($"Worksheet name: {worksheet.Name}");
        Console.WriteLine($"Last row used: {worksheet.LastRowUsed()?.RowNumber()}");
        Console.WriteLine($"Last column used: {worksheet.LastColumnUsed()?.ColumnNumber()}");
        Console.WriteLine($"\nFirst 15 rows:");
        Console.WriteLine(new string('-', 150));

        for (int row = 1; row <= Math.Min(15, worksheet.LastRowUsed()?.RowNumber() ?? 0); row++)
        {
            var cells = new List<string>();
            for (int col = 1; col <= (worksheet.LastColumnUsed()?.ColumnNumber() ?? 8); col++)
            {
                var cell = worksheet.Cell(row, col);
                var value = cell.Value.ToString() ?? "";
                cells.Add(value);
            }
            Console.WriteLine($"Row {row,2}: {string.Join(" | ", cells)}");
        }

        Console.WriteLine(new string('-', 150));
        Console.WriteLine($"\nFreeze panes: Row {worksheet.SheetView.FreezeRows}, Col {worksheet.SheetView.FreezeColumns}");
    }
}
