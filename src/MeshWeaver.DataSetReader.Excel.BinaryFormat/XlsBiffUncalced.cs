// /******************************************************************************************************
//  * Copyright (c) 2012- Systemorph Ltd. This file is part of Systemorph Platform. All rights reserved. *
//  ******************************************************************************************************/
namespace MeshWeaver.DataSetReader.Excel.BinaryFormat
{
    /// <summary>
    /// If present the Calculate Message was in the status bar when Excel saved the file.
    /// This occurs if the sheet changed, the Manual calculation option was on, and the Recalculate Before Save option was off.    
    /// </summary>
    internal class XlsBiffUncalced : XlsBiffRecord
    {
        internal XlsBiffUncalced(byte[] bytes, uint offset, ExcelBinaryReader reader)
            : base(bytes, offset, reader)
        {
        }

    }
}