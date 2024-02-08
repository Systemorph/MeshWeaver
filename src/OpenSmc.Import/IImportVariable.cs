﻿using System.ComponentModel.DataAnnotations;
using OpenSmc.DataSource.Abstractions;
using OpenSmc.DataStructures;
using OpenSmc.DomainDesigner.Abstractions;
using OpenSmc.FileStorage;
using OpenSmc.Import.Contract.Builders;
using OpenSmc.Import.Contract.Options;

namespace OpenSmc.Import
{
    public interface IImportVariable
    {
        public FileReaderImportOptionsBuilder FromFile(string filePath);
        public StringImportOptionsBuilder FromString(string content);
        public StreamImportOptionsBuilder FromStream(Stream stream);
        public DataSetImportOptionsBuilder FromDataSet(IDataSet dataSet);
        public void SetDefaultFileStorage(IFileReadStorage storage);
        public void SetDefaultDomain(DomainDescriptor domain);
        public void SetDefaultTarget(IDataSource target);
        public void SetDefaultValidation(Func<object, ValidationContext, Task<bool>> validationRule);
        public void SetDefaultValidation(Func<object, ValidationContext, bool> validationRule);
        void DefineFormat(string format, Func<ImportOptions, IDataSet, Task> importFunction);
    }
}
