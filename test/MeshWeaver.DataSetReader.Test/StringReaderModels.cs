using System;
using System.Collections.Generic;

namespace MeshWeaver.DataSetReader.Test;

public record TestImportEntityWithOrder([property: MappingOrder(3)] DateTime DateTimeProperty,
                                        [property: MappingOrder(1)] decimal DecimalProperty,
                                        [property: MappingOrder(0)] double DoubleProperty,
                                        int IntProperty,
                                        [property: MappingOrder(2)] string StringProperty);

public record TestImportEntityWithListsAndOrder([property: MappingOrder(0)] double DoubleProperty,
                                                [property: MappingOrder(1, Length = 4)] IList<int> ListOfIntegers,
                                                [property: MappingOrder(2)] string StringProperty);

public record TestImportEntityWithListWithoutLength([property: MappingOrder(0)] double DoubleProperty,
                                                    [property: MappingOrder(1)] IList<int> ListOfIntegers,
                                                    [property: MappingOrder(2)] string StringProperty);
