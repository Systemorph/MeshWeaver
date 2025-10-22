using MeshWeaver.Insurance.SampleData;

var excelPath = Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory,
    "..", "..", "..", "..",
    "Files", "Microsoft", "2026", "Microsoft.xlsx"
);

if (args.Length > 0 && args[0] == "inspect")
{
    Console.WriteLine($"Inspecting Excel file: {excelPath}");
    if (!File.Exists(excelPath))
    {
        Console.WriteLine($"Error: File not found at {excelPath}");
        return 1;
    }
    ExcelInspector.InspectFile(excelPath);
}
else
{
    Console.WriteLine($"Updating Excel file: {excelPath}");
    if (!File.Exists(excelPath))
    {
        Console.WriteLine($"Error: File not found at {excelPath}");
        return 1;
    }

    // Create backup
    var backupPath = excelPath.Replace(".xlsx", $".backup.{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    File.Copy(excelPath, backupPath, true);
    Console.WriteLine($"Backup created: {backupPath}");

    MicrosoftExcelGenerator.GenerateMicrosoftData(excelPath);
    Console.WriteLine("Successfully updated Microsoft.xlsx with 65+ locations including Chinese and Japanese offices!");
    Console.WriteLine("Preserved: header rows with totals and freeze panes");
}

return 0;
