using ClosedXML.Excel;

namespace MeshWeaver.Insurance.SampleData;

public class MicrosoftExcelGenerator
{
    public static void GenerateMicrosoftData(string filePath)
    {
        using var workbook = new XLWorkbook(filePath);
        var worksheet = workbook.Worksheets.First();

        // Find the header row (look for a row that has column headers)
        int headerRow = FindHeaderRow(worksheet);
        int firstDataRow = headerRow + 1;

        // Note: We'll re-establish freeze panes after updating

        // Clear existing data rows (keep everything above and including header row)
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;
        if (lastRow >= firstDataRow)
        {
            worksheet.Rows(firstDataRow, lastRow).Delete();
        }

        var microsoftLocations = GetMicrosoftLocations();

        int currentRow = firstDataRow;
        foreach (var location in microsoftLocations)
        {
            worksheet.Cell(currentRow, 1).Value = location.Name;
            worksheet.Cell(currentRow, 2).Value = location.Type;
            worksheet.Cell(currentRow, 3).Value = location.Address;
            worksheet.Cell(currentRow, 4).Value = location.City;
            worksheet.Cell(currentRow, 5).Value = location.Country;
            worksheet.Cell(currentRow, 6).Value = location.Employees;
            worksheet.Cell(currentRow, 7).Value = location.Revenue;
            worksheet.Cell(currentRow, 8).Value = location.OpenDate;
            currentRow++;
        }

        // Update totals in the first few rows if they exist
        UpdateTotals(worksheet, headerRow, firstDataRow, currentRow - 1);

        // Set freeze panes (freeze at header row)
        if (headerRow > 0)
        {
            worksheet.SheetView.FreezeRows(headerRow);
            worksheet.SheetView.FreezeColumns(0);
        }

        workbook.Save();
    }

    private static int FindHeaderRow(IXLWorksheet worksheet)
    {
        // Look for a row that contains typical header text
        for (int row = 1; row <= 10; row++)
        {
            var firstCell = worksheet.Cell(row, 1).Value.ToString().ToLower();
            // Common header patterns
            if (firstCell.Contains("name") || firstCell.Contains("location") ||
                firstCell.Contains("office") || firstCell == "name")
            {
                return row;
            }
        }
        return 1; // Default to row 1 if not found
    }

    private static void UpdateTotals(IXLWorksheet worksheet, int headerRow, int firstDataRow, int lastDataRow)
    {
        // Update totals in rows before the header (if they exist)
        for (int row = 1; row < headerRow; row++)
        {
            var cell = worksheet.Cell(row, 1).Value.ToString().ToLower();
            if (cell.Contains("total") || cell.Contains("sum"))
            {
                // Update numeric totals for columns 6 (Employees) and 7 (Revenue)
                var employeesCell = worksheet.Cell(row, 6);
                var revenueCell = worksheet.Cell(row, 7);

                if (firstDataRow <= lastDataRow)
                {
                    employeesCell.FormulaA1 = $"=SUM({worksheet.Cell(firstDataRow, 6).Address}:{worksheet.Cell(lastDataRow, 6).Address})";
                    revenueCell.FormulaA1 = $"=SUM({worksheet.Cell(firstDataRow, 7).Address}:{worksheet.Cell(lastDataRow, 7).Address})";
                }
            }
        }
    }

    private static List<MicrosoftLocation> GetMicrosoftLocations()
    {
        return new List<MicrosoftLocation>
        {
            // North America
            new("Microsoft Campus - Redmond", "Headquarters", "1 Microsoft Way", "Redmond, WA", "United States", 50000, 125000000, new DateTime(1986, 2, 26)),
            new("Microsoft Silicon Valley Campus", "Office", "1065 La Avenida", "Mountain View, CA", "United States", 2500, 8500000, new DateTime(2015, 7, 1)),
            new("Microsoft New York", "Office", "11 Times Square", "New York, NY", "United States", 3200, 12000000, new DateTime(2015, 3, 15)),
            new("Microsoft Cambridge", "Research Lab", "1 Memorial Drive", "Cambridge, MA", "United States", 850, 2100000, new DateTime(2008, 9, 1)),
            new("Microsoft Vancouver", "Office", "725 Granville St", "Vancouver, BC", "Canada", 750, 2800000, new DateTime(2018, 5, 1)),
            new("Microsoft Toronto", "Office", "81 Bay Street", "Toronto, ON", "Canada", 1200, 4200000, new DateTime(2012, 11, 1)),
            new("Microsoft Atlanta Data Center", "Data Center", "6789 Peachtree Rd", "Atlanta, GA", "United States", 180, 950000, new DateTime(2019, 3, 15)),
            new("Microsoft Chicago", "Sales Point", "200 E Randolph St", "Chicago, IL", "United States", 680, 2400000, new DateTime(2016, 8, 20)),
            new("Microsoft San Antonio Data Center", "Data Center", "10000 Interstate 10 W", "San Antonio, TX", "United States", 220, 1100000, new DateTime(2020, 1, 10)),
            new("Microsoft Miami", "Sales Point", "1001 Brickell Bay Dr", "Miami, FL", "United States", 420, 1600000, new DateTime(2017, 4, 5)),

            // Europe
            new("Microsoft UK Headquarters", "Office", "2 Kingdom Street", "London", "United Kingdom", 3800, 15000000, new DateTime(2004, 5, 1)),
            new("Microsoft Ireland", "Office", "One Microsoft Place", "Dublin", "Ireland", 2200, 7800000, new DateTime(2003, 10, 1)),
            new("Microsoft France", "Office", "39 Quai du Président Roosevelt", "Paris", "France", 1800, 6500000, new DateTime(1983, 7, 1)),
            new("Microsoft Germany", "Office", "Konrad-Zuse-Straße 1", "Munich", "Germany", 2800, 9200000, new DateTime(1983, 3, 1)),
            new("Microsoft Netherlands", "Office", "Evert van de Beekstraat 354", "Amsterdam", "Netherlands", 980, 3400000, new DateTime(1989, 6, 1)),
            new("Microsoft Switzerland", "Office", "Richtistrasse 3", "Zurich", "Switzerland", 720, 2900000, new DateTime(1988, 11, 1)),
            new("Microsoft Sweden", "Office", "Kungsbron 2", "Stockholm", "Sweden", 650, 2200000, new DateTime(1985, 4, 1)),
            new("Microsoft Dublin Data Center", "Data Center", "Parkwest Business Park", "Dublin", "Ireland", 150, 680000, new DateTime(2009, 6, 1)),
            new("Microsoft Amsterdam Data Center", "Data Center", "Science Park 140", "Amsterdam", "Netherlands", 190, 820000, new DateTime(2014, 9, 1)),
            new("Microsoft Spain", "Office", "Calle de Josefa Valcárcel 42", "Madrid", "Spain", 890, 3100000, new DateTime(1992, 2, 1)),

            // Asia-Pacific
            new("Microsoft 中国 - 北京", "Office", "丹棱街5号", "北京", "China", 4200, 18000000, new DateTime(1995, 5, 1)),
            new("Microsoft 中国 - 上海", "Office", "紫竹科学园区", "上海", "China", 3100, 12500000, new DateTime(2003, 9, 1)),
            new("Microsoft 中国 - 深圳", "Office", "科技南路16号", "深圳", "China", 1800, 7200000, new DateTime(2012, 3, 1)),
            new("Microsoft 日本 - 東京", "Office", "港区港南 2-16-3", "東京", "Japan", 2500, 9800000, new DateTime(1986, 2, 1)),
            new("Microsoft 日本 - 大阪", "Sales Point", "大阪市北区梅田3-3-10", "大阪", "Japan", 680, 2600000, new DateTime(2010, 7, 1)),
            new("Microsoft Singapore", "Office", "1 Marina Boulevard", "Singapore", "Singapore", 1200, 5400000, new DateTime(1990, 8, 1)),
            new("Microsoft Singapore Data Center", "Data Center", "25 Tai Seng Street", "Singapore", "Singapore", 140, 720000, new DateTime(2013, 11, 1)),
            new("Microsoft India - Bangalore", "Office", "RMZ Infinity Tower E", "Bangalore", "India", 8500, 16000000, new DateTime(1990, 12, 1)),
            new("Microsoft India - Hyderabad", "Office", "iLabs Centre", "Hyderabad", "India", 6200, 11500000, new DateTime(1998, 3, 1)),
            new("Microsoft India - Noida", "Sales Point", "Express Trade Towers", "Noida", "India", 950, 3200000, new DateTime(2016, 1, 1)),
            new("Microsoft Australia - Sydney", "Office", "1 Epping Road", "Sydney", "Australia", 1800, 6200000, new DateTime(1983, 11, 1)),
            new("Microsoft South Korea - 서울", "Office", "강남구 테헤란로 152", "서울", "South Korea", 1100, 4800000, new DateTime(1989, 1, 1)),
            new("Microsoft Taiwan - 台北", "Office", "信義路五段7號", "台北", "Taiwan", 780, 2900000, new DateTime(1989, 10, 1)),
            new("Microsoft Hong Kong - 香港", "Office", "太古坊德宏大廈", "香港", "Hong Kong", 620, 2400000, new DateTime(1991, 5, 1)),
            new("Microsoft Malaysia - Kuala Lumpur", "Sales Point", "Level 26, Menara 3 Petronas", "Kuala Lumpur", "Malaysia", 380, 1400000, new DateTime(2008, 4, 1)),
            new("Microsoft Philippines - Manila", "Sales Point", "8th Floor, 6750 Ayala Avenue", "Manila", "Philippines", 420, 1600000, new DateTime(2006, 9, 1)),
            new("Microsoft Thailand - กรุงเทพมหานคร", "Sales Point", "589 Sukhumvit Road", "กรุงเทพมหานคร", "Thailand", 310, 1200000, new DateTime(2011, 6, 1)),
            new("Microsoft Indonesia - Jakarta", "Sales Point", "Wisma Mulia, Sudirman", "Jakarta", "Indonesia", 280, 1050000, new DateTime(2013, 2, 1)),
            new("Microsoft Vietnam - Hồ Chí Minh", "Sales Point", "Bitexco Financial Tower", "Hồ Chí Minh", "Vietnam", 240, 890000, new DateTime(2015, 8, 1)),

            // Middle East & Africa
            new("Microsoft UAE - Dubai", "Office", "Dubai Internet City", "Dubai", "United Arab Emirates", 680, 2800000, new DateTime(2003, 5, 1)),
            new("Microsoft South Africa - Johannesburg", "Office", "3012 William Nicol Drive", "Johannesburg", "South Africa", 580, 2100000, new DateTime(1992, 9, 1)),
            new("Microsoft Israel - Tel Aviv", "Office", "94 Em HaMoshavot Road", "Tel Aviv", "Israel", 1200, 4500000, new DateTime(1989, 6, 1)),
            new("Microsoft Egypt - القاهرة", "Sales Point", "Smart Village, Building B141", "القاهرة", "Egypt", 320, 1150000, new DateTime(2007, 3, 1)),
            new("Microsoft Qatar Data Center", "Data Center", "Doha Technology Park", "Doha", "Qatar", 110, 580000, new DateTime(2018, 5, 1)),

            // South America
            new("Microsoft Brazil - São Paulo", "Office", "Avenida Presidente Juscelino Kubitschek 1830", "São Paulo", "Brazil", 1400, 5200000, new DateTime(1989, 3, 1)),
            new("Microsoft Argentina - Buenos Aires", "Sales Point", "Ing. Enrique Butty 240", "Buenos Aires", "Argentina", 380, 1400000, new DateTime(1994, 7, 1)),
            new("Microsoft Colombia - Bogotá", "Sales Point", "Carrera 7 No. 71-21", "Bogotá", "Colombia", 290, 1080000, new DateTime(2010, 11, 1)),
            new("Microsoft Chile - Santiago", "Sales Point", "Av. Andrés Bello 2711", "Santiago", "Chile", 240, 920000, new DateTime(2012, 4, 1)),
            new("Microsoft Mexico - Ciudad de México", "Office", "Paseo de la Reforma 505", "Ciudad de México", "Mexico", 850, 3300000, new DateTime(1991, 8, 1)),

            // Additional Data Centers
            new("Microsoft Iowa Data Center", "Data Center", "3131 114th Street", "West Des Moines, IA", "United States", 165, 780000, new DateTime(2007, 4, 1)),
            new("Microsoft Virginia Data Center", "Data Center", "44801 Gloucester Parkway", "Ashburn, VA", "United States", 210, 980000, new DateTime(2010, 9, 1)),
            new("Microsoft Wyoming Data Center", "Data Center", "3401 E Lincolnway", "Cheyenne, WY", "United States", 125, 620000, new DateTime(2012, 6, 1)),
            new("Microsoft Arizona Data Center", "Data Center", "1950 N Alma School Road", "Chandler, AZ", "United States", 185, 850000, new DateTime(2015, 2, 1)),
            new("Microsoft Amsterdam 2 Data Center", "Data Center", "Kabelweg 37", "Amsterdam", "Netherlands", 145, 690000, new DateTime(2017, 10, 1)),
            new("Microsoft Finland Data Center", "Data Center", "Metsäläntie 7", "Helsinki", "Finland", 95, 480000, new DateTime(2018, 12, 1)),
            new("Microsoft Norway Data Center", "Data Center", "Drammensvegen 264", "Oslo", "Norway", 102, 520000, new DateTime(2019, 8, 1)),
            new("Microsoft South Africa Data Center", "Data Center", "Longkloof Studios", "Cape Town", "South Africa", 88, 420000, new DateTime(2020, 3, 1)),
            new("Microsoft Australia Data Center", "Data Center", "51 Bourke Road", "Melbourne", "Australia", 118, 580000, new DateTime(2014, 5, 1)),
            new("Microsoft New Zealand - Auckland", "Sales Point", "157 Lambton Quay", "Auckland", "New Zealand", 180, 680000, new DateTime(2009, 9, 1)),

            // Additional Asian Locations
            new("Microsoft 中国 - 广州", "Sales Point", "天河区珠江新城", "广州", "China", 720, 2800000, new DateTime(2015, 6, 1)),
            new("Microsoft 中国 - 成都", "Sales Point", "高新区天府大道", "成都", "China", 580, 2200000, new DateTime(2017, 9, 1)),
            new("Microsoft 中国 - 杭州", "Sales Point", "西湖区文三路", "杭州", "China", 640, 2500000, new DateTime(2016, 4, 1)),
            new("Microsoft 日本 - 名古屋", "Sales Point", "中村区名駅4-7-1", "名古屋", "Japan", 280, 1100000, new DateTime(2014, 3, 1)),
            new("Microsoft 日本 - 福岡", "Sales Point", "博多区博多駅前2-1-1", "福岡", "Japan", 195, 780000, new DateTime(2016, 11, 1)),
        };
    }

    private record MicrosoftLocation(
        string Name,
        string Type,
        string Address,
        string City,
        string Country,
        int Employees,
        decimal Revenue,
        DateTime OpenDate
    );
}
