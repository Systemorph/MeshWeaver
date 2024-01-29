using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;
using OpenSmc.Layout;
using static OpenSmc.DomainDesigner.ExcelParser.DocumentHelperExtensions;
using static OpenSmc.DataSetReader.Excel.Utils.ExcelConstants;
using static OpenSmc.DataSetReader.Excel.Utils.Helpers;

namespace OpenSmc.DomainDesigner.ExcelParser.DocumentParsing
{
    public class ExcelStyleFormatParser : IDocumentStyleFormatParser
    {
        // /t contains too much spaces
        public const string TabSing = "    ";
        public const string ExcludedSign = @"//";

        //actual position => numFmtId
        private IDictionary<int, int> stylingMap;
        //numFmtId => formatCode
        private IDictionary<int, string> customStylingMap;
        //id => item value
        private IDictionary<int, string> sharedStringItems;

        public async Task<CodeSample> ParseDocumentAsync(string filePath, IFileReadStorage storage, string domainName)
        {
            await using var stream = await storage.ReadAsync(filePath);
            using var document = SpreadsheetDocument.Open(stream, false);

            Initialize(document);

            //type name => (propType, propName)
            var typesToProperties = new Dictionary<string, IList<PropertyDescriptor>>();

            foreach (var sheet in document.WorkbookPart.Workbook.Descendants<Sheet>())
            {
                var sheetData = (document.WorkbookPart.GetPartById(sheet.Id) as WorksheetPart)?.Worksheet.GetFirstChild<SheetData>();

                if (sheetData == null)
                {
                    throw new ArgumentNullException($"Id {sheet.Id} doesn't find any corresponding sheet.");
                }

                var sheetName = sheet.Name.ToString();
                if (Regex.IsMatch(sheetName, NamingPattern))
                {
                    //logProvider.Warn(new WarningNotification($"Sheet name: {sheetName} contains illegal characters and would be repaired."));
                    //TODO: keep old name as attribute?
                    sheetName = Regex.Replace(sheetName, NamingPattern, string.Empty);
                }

                var headerRow = sheetData.Descendants<Row>().FirstOrDefault();

                //empty header row means record with no properties
                if (headerRow == null)
                {
                    //logProvider.Warn(new WarningNotification($"Sheet: {sheetName} doesn't contains any populated rows."));
                    typesToProperties.TryAdd(sheetName, new List<PropertyDescriptor>());
                    continue;
                }

                //cell ref => cell value
                var headerProperties = HeaderRowParsing(headerRow);

                /*if (logProvider.HasError())
                    continue;*/

                typesToProperties.TryAdd(sheetName, ContentRowParsing(headerProperties, sheetData.Descendants<Row>().Skip(1).Take(1).FirstOrDefault()));
            }

            var stringCode = PerformTypeRepresentation(typesToProperties, domainName);

            return new CodeSample(stringCode);
        }

        private string PerformTypeRepresentation(Dictionary<string, IList<PropertyDescriptor>> typesToProperties, string domainName)
        {
            var domainBuilder = new StringBuilder($@"var domain = Domain.CreateDomain(""{domainName}"")");
            var recordBuilder = new StringBuilder();

            foreach (var (recordName, properties) in typesToProperties)
            {
                recordBuilder.Append($"public record {recordName}{Environment.NewLine}{{{Environment.NewLine}");
                domainBuilder.Append($".WithType<{recordName}>()");

                if (properties == null || !properties.Any())
                {
                    recordBuilder.Append($"}}{Environment.NewLine}");
                    continue;
                }

                foreach (var property in properties)
                {
                    if (property.ParsedPropName != property.BasicPropName)
                        recordBuilder.Append($@"{TabSing}{(property.Excluded ? ExcludedSign : string.Empty)}[MapTo(""{property.BasicPropName}"")]{Environment.NewLine}");

                    if (property.Excluded)
                        recordBuilder.Append($"{TabSing}{(property.Excluded ? ExcludedSign : string.Empty)}type of cells inconsistent{Environment.NewLine}");

                    var propType = property.PropType.IsAssignableFrom(typeof(int))
                                       ? "int"
                                       : property.PropType.IsAssignableFrom(typeof(long))
                                           ? "long"
                                           : property.PropType.IsAssignableFrom(typeof(float))
                                               ? "float"
                                               : property.PropType.IsAssignableFrom(typeof(DateTime))
                                                   ? nameof(DateTime)
                                                   : property.PropType.Name.ToLower();

                    recordBuilder.Append($"{TabSing}{(property.Excluded ? ExcludedSign : string.Empty)}public {(property.IsList ? "IList<" + propType + ">" : propType)} {property.ParsedPropName} " +
                                         $"{{ get; init; }}{Environment.NewLine}");
                }


                recordBuilder.Append($"}}{Environment.NewLine}");
            }

            domainBuilder.Append(".ToDomain();");
            return recordBuilder.AppendJoin("\n", domainBuilder).ToString();
        }

        private void Initialize(SpreadsheetDocument document)
        {
            BuildStylingMap(document.WorkbookPart.WorkbookStylesPart);
            BuildSharedStringMap(document.WorkbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault()?.SharedStringTable);
        }

        private void BuildSharedStringMap(SharedStringTable sharedStringTable)
        {
            sharedStringItems = new Dictionary<int, string>();

            if (sharedStringTable == null)
                return;

            var sharedItems = sharedStringTable.Elements<SharedStringItem>().ToList();

            for (var i = 0; i < sharedItems.Count; i++)
                sharedStringItems.TryAdd(i, sharedItems[i].InnerText);
        }

        private void BuildStylingMap(WorkbookStylesPart stylePart)
        {
            stylingMap = new Dictionary<int, int>();
            customStylingMap = new Dictionary<int, string>();
            if (stylePart == null)
                return;

            var cellFormatParentNodes = stylePart.Stylesheet.ChildElements.OfType<CellFormats>();

            foreach (var parentNode in cellFormatParentNodes)
            {
                var formatNodes = parentNode.ChildElements.OfType<CellFormat>().ToList();
                var numFormatNodes = stylePart.Stylesheet.ChildElements.OfType<NumberingFormats>().FirstOrDefault()?
                    .ChildElements.OfType<NumberingFormat>().ToList() ?? new List<NumberingFormat>();

                for (var i = 0; i < formatNodes.Count; i++)
                {
                    var formatId = Convert.ToInt32(formatNodes[i].NumberFormatId.Value);
                    //164 is a lower border of the custom format
                    if (formatId >= 164)
                    {
                        //in case of corrupted document, custom formatCode would set as string
                        var formatCode = numFormatNodes.FirstOrDefault(x => x.NumberFormatId == formatId)?.FormatCode ?? "@";
                        customStylingMap.TryAdd(formatId, formatCode);
                        stylingMap.TryAdd(i, formatId);
                    }
                    else
                        stylingMap.TryAdd(i, formatId);
                }
            }
        }

        private IDictionary<string, PropertyDescriptor> HeaderRowParsing(Row headerRow)
        {
            var headers = new Dictionary<string, PropertyDescriptor>();

            var previousCellReference = char.MinValue;

            foreach (var cell in headerRow.Descendants<Cell>())
            {
                //check that first header row is not empty
                if (previousCellReference == char.MinValue && cell.CellValue == null )
                    return headers;

                var numberFreeCellRef = Regex.Replace(cell.CellReference.Value, NumberPattern, "");
                var cellRef = numberFreeCellRef.Last(); //take last letter for ref cases such AA, ABC etc.

                //we stop parsing column in case of the any gaps in between (NOTE: for openXml there is no gaps);
                //example: A,B,C,D,F => res: A,B,C,D columns would be taken
                if (!CellInAlphabeticalOrder(previousCellReference, cellRef) || cell.CellValue == null)
                    return headers;

                previousCellReference = cellRef;

                var cellValue = cell.CellValue.Text;
                string parsedValue;

                if (cell.DataType == null || cell.DataType.Value != CellValues.SharedString)
                {
                    parsedValue = MatchRegexNamingPattern(cellValue, NamingPattern);

                    if (parsedValue == string.Empty)
                    {
                        //logProvider.Warn(new WarningNotification($"Column {cell.CellReference.Value} has invalid value and can not be parsed as property: {cellValue}"));
                        continue;
                    }

                    headers.TryAdd(parsedValue, new PropertyDescriptor
                    {
                        ParsedPropName = parsedValue,
                        BasicPropName = cellValue,
                        CellRef = numberFreeCellRef,
                        IsList = Regex.Match(parsedValue, NumberAtEndPattern).Success
                    });
                }
                else
                {
                    if (!int.TryParse(cellValue, out var sharedIdx) || !sharedStringItems.TryGetValue(sharedIdx, out var sharedValue))
                        throw new ParsingException($"Cell value marked as shared, but his index: {cellValue} is not present in global store or could not be parsed properly.");
                    
                    parsedValue = MatchRegexNamingPattern(sharedValue, NamingPattern);
                    if (parsedValue == string.Empty)
                    {
                        //logProvider.Warn(new WarningNotification($"Column {cell.CellReference.Value} has invalid value and can not be parsed as property: {cellValue}"));
                        continue;
                    }

                    headers.TryAdd(parsedValue, new PropertyDescriptor
                                                {
                                                    ParsedPropName = parsedValue,
                                                    BasicPropName = sharedValue,
                                                    CellRef = numberFreeCellRef,
                                                    IsList = Regex.Match(parsedValue, NumberAtEndPattern).Success
                                                });
                }
            }

            return headers;
        }

        private IList<PropertyDescriptor> ContentRowParsing(IDictionary<string, PropertyDescriptor> headers, Row contentRow)
        {
            if (!headers.Any())
                return new List<PropertyDescriptor>();

            var refToStyleIdx = contentRow?.Descendants<Cell>()?
                                .ToDictionary(x => Regex.Replace(x.CellReference.Value, NumberPattern, ""),
                                              y => y.StyleIndex?.Value) ?? new Dictionary<string, uint?>();
            
            var nonListHeader = headers.Where(x => !x.Value.IsList).Select(x => x.Value).ToList();

            for (var i = 0; i < nonListHeader.Count; i++)
            {
                var header = nonListHeader[i];

                if (!refToStyleIdx.TryGetValue(header.CellRef, out var styleIdx) || styleIdx == null)
                {
                    nonListHeader[i] = header with { PropType = typeof(string) };
                }
                else
                {
                    stylingMap.TryGetValue(Convert.ToInt32(styleIdx), out var styleValue);
                    customStylingMap.TryGetValue(styleValue, out var formatCode);
                    nonListHeader[i] = header with { PropType = string.IsNullOrEmpty(formatCode) ? NumberingFormatDecoder.Decode(styleValue) : NumberingFormatDecoder.Decode(formatCode) };
                }
            }

            var listHeaders = headers.Where(x => x.Value.IsList)
                                     .Select(x => x.Value with
                                                  {
                                                      ParsedPropName = Regex.Replace(x.Value.ParsedPropName, "[0-9]*$", string.Empty),
                                                      BasicPropName = Regex.Replace(x.Value.BasicPropName, "[0-9]*$", string.Empty),
                                                      PropType = !refToStyleIdx.TryGetValue(x.Value.CellRef, out var styleIdx) || styleIdx == null
                                                                     ? typeof(string)
                                                                     : stylingMap.TryGetValue(Convert.ToInt32(styleIdx), out var styleValue)
                                                                         ? customStylingMap.TryGetValue(styleValue, out var formatCode)
                                                                               ? NumberingFormatDecoder.Decode(formatCode)
                                                                               : NumberingFormatDecoder.Decode(styleValue)
                                                                         : typeof(string)
                                                  }).ToList();

            var differHeaderColumns = listHeaders.GroupBy(x => x.ParsedPropName)
                                                 .Where(x => x.Select(y => y.PropType).Distinct().Count() > 1)
                                                 .Select(x => x.Key).Distinct().ToList();

            var filteredListHeaders = listHeaders.DistinctBy(x => x.ParsedPropName).ToList();

            //mark List property as excluded in case of different types of cells
            if (differHeaderColumns.Any())
                for (var i = 0; i < filteredListHeaders.Count; i++)
                    if (differHeaderColumns.Contains(filteredListHeaders[i].ParsedPropName))
                        filteredListHeaders[i] = filteredListHeaders[i] with { Excluded = true };

            //handle cases when list property has the same name as non list property
            foreach (var crossEntry in nonListHeader.Select(x => x.ParsedPropName).Intersect(filteredListHeaders.Select(x => x.ParsedPropName)))
            {
                throw new ParsingException($"List and non list property would have the same names: {crossEntry}. Such behaviour not allowed.");
            }

            return /*logProvider.HasError() ? new List<PropertyDescriptor>() : */ nonListHeader.Concat(filteredListHeaders).ToList();
        }
    }
}
