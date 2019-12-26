﻿using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using WeihanLi.Extensions;
using WeihanLi.Npoi.Abstract;
using WeihanLi.Npoi.Configurations;
using WeihanLi.Npoi.Settings;

namespace WeihanLi.Npoi
{
    internal static class InternalExcelHelper
    {
        private static SheetSetting GetSheetSetting(IDictionary<int, SheetSetting> sheetSettings, int sheetIndex)
        {
            return sheetIndex >= 0 && sheetSettings.ContainsKey(sheetIndex)
                ? sheetSettings[sheetIndex]
                : sheetSettings[0];
        }

        public static List<TEntity> SheetToEntityList<TEntity>([NotNull]ISheet sheet, int sheetIndex) where TEntity : new()
        {
            var entityType = typeof(TEntity);
            var configuration = InternalHelper.GetExcelConfigurationMapping<TEntity>();
            var sheetSetting = GetSheetSetting(configuration.SheetSettings, sheetIndex);
            var entities = new List<TEntity>(sheet.LastRowNum - sheetSetting.HeaderRowIndex);

            var propertyColumnDictionary = InternalHelper.GetPropertyColumnDictionary(configuration);
            var propertyColumnDic = sheetSetting.HeaderRowIndex >= 0
                ? propertyColumnDictionary.ToDictionary(_ => _.Key, _ => new PropertyConfiguration()
                {
                    ColumnIndex = -1,
                    ColumnFormatter = _.Value.ColumnFormatter,
                    ColumnTitle = _.Value.ColumnTitle,
                    ColumnWidth = _.Value.ColumnWidth,
                    IsIgnored = _.Value.IsIgnored
                })
                : propertyColumnDictionary;

            for (var rowIndex = sheet.FirstRowNum - 1; rowIndex < sheet.LastRowNum; rowIndex++)
            {
                var row = sheet.GetRow(rowIndex);
                if (rowIndex == sheetSetting.HeaderRowIndex) // readerHeader
                {
                    if (row != null)
                    {
                        for (var i = 0; i < row.LastCellNum; i++)
                        {
                            if (row.GetCell(i) == null)
                            {
                                continue;
                            }
                            var col = propertyColumnDic.GetPropertySetting(row.GetCell(i).Value.ToString().Trim());
                            if (null != col)
                            {
                                col.ColumnIndex = i;
                            }
                        }
                    }
                    //
                    if (propertyColumnDic.Values.All(_ => _.ColumnIndex < 0))
                    {
                        propertyColumnDic = propertyColumnDictionary;
                    }
                }
                else if (rowIndex >= sheetSetting.StartRowIndex)
                {
                    if (row == null)
                    {
                        entities.Add(default);
                    }
                    else
                    {
                        TEntity entity;
                        if (row.CellsCount > 0)
                        {
                            entity = new TEntity();

                            if (entityType.IsValueType)
                            {
                                var obj = (object)entity;// boxing for value types
                                foreach (var key in propertyColumnDic.Keys)
                                {
                                    var colIndex = propertyColumnDic[key].ColumnIndex;
                                    if (colIndex >= 0)
                                    {
                                        key.GetValueSetter()?.Invoke(obj, row.GetCell(colIndex).GetCellValue(key.PropertyType));
                                    }
                                }
                                entity = (TEntity)obj;// unboxing
                            }
                            else
                            {
                                foreach (var key in propertyColumnDic.Keys)
                                {
                                    var colIndex = propertyColumnDic[key].ColumnIndex;
                                    if (colIndex >= 0)
                                    {
                                        key.GetValueSetter()?.Invoke(entity, row.GetCell(colIndex).GetCellValue(key.PropertyType));
                                    }
                                }
                            }
                        }
                        else
                        {
                            entity = default;
                        }

                        if (null != entity)
                        {
                            foreach (var propertyInfo in propertyColumnDic.Keys)
                            {
                                if (propertyInfo.CanWrite)
                                {
                                    var propertyValue = propertyInfo.GetValueGetter()?.Invoke(entity);
                                    var formatterFunc = InternalCache.InputFormatterFuncCache.GetOrAdd(propertyInfo, p =>
                                    {
                                        var propertySettingType = typeof(PropertyConfiguration<,>).MakeGenericType(entityType, p.PropertyType);
                                        return propertySettingType.GetProperty(InternalConstants.InputFormatterFuncName)?.GetValueGetter()?.Invoke(propertyColumnDictionary[propertyInfo]);
                                    });
                                    if (null != formatterFunc)
                                    {
                                        var funcType = typeof(Func<,,>).MakeGenericType(entityType, propertyInfo.PropertyType, typeof(object));
                                        var method = funcType.GetProperty("Method")?.GetValueGetter()?.Invoke(formatterFunc) as MethodInfo;
                                        var target = funcType.GetProperty("Target")?.GetValueGetter()?.Invoke(formatterFunc);

                                        if (null != method && target != null)
                                        {
                                            try
                                            {
                                                // apply custom formatterFunc
                                                var formattedValue = method.Invoke(target, new[] { entity, propertyValue });
                                                propertyInfo.GetValueSetter().Invoke(entity, formattedValue);
                                            }
                                            catch (Exception e)
                                            {
                                                Debug.WriteLine(e);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        entities.Add(entity);
                    }
                }
            }

            return entities;
        }

        public static ISheet EntityListToSheet<TEntity>([NotNull]ISheet sheet, IEnumerable<TEntity> entityList, int sheetIndex) where TEntity : new()
        {
            if (null == entityList)
            {
                return sheet;
            }
            var entityType = typeof(TEntity);
            var configuration = InternalHelper.GetExcelConfigurationMapping<TEntity>();
            var propertyColumnDictionary = InternalHelper.GetPropertyColumnDictionary(configuration);
            if (propertyColumnDictionary.Keys.Count == 0)
            {
                return sheet;
            }

            var sheetSetting = GetSheetSetting(configuration.SheetSettings, sheetIndex);
            if (sheetSetting.HeaderRowIndex >= 0)
            {
                var headerRow = sheet.CreateRow(sheetSetting.HeaderRowIndex);
                foreach (var key in propertyColumnDictionary.Keys)
                {
                    headerRow.CreateCell(propertyColumnDictionary[key].ColumnIndex)
                        .SetCellValue(propertyColumnDictionary[key].ColumnTitle);
                }
            }

            var rowIndex = 0;
            foreach (var entity in entityList)
            {
                var row = sheet.CreateRow(sheetSetting.StartRowIndex + rowIndex);
                if (entity != null)
                {
                    foreach (var key in propertyColumnDictionary.Keys)
                    {
                        var propertyValue = key.GetValueGetter<TEntity>()?.Invoke(entity);
                        var formatterFunc = InternalCache.OutputFormatterFuncCache.GetOrAdd(key, p =>
                        {
                            var propertyConfigurationType = typeof(PropertyConfiguration<,>).MakeGenericType(entityType, p.PropertyType);
                            return propertyConfigurationType.GetProperty(InternalConstants.OutputFormatterFuncName)?.GetValueGetter()?.Invoke(propertyColumnDictionary[key]);
                        });
                        if (null != formatterFunc)
                        {
                            var funcType = typeof(Func<,,>).MakeGenericType(entityType, key.PropertyType, typeof(object));
                            var method = funcType.GetProperty("Method")?.GetValueGetter()?.Invoke(formatterFunc) as MethodInfo;
                            var target = funcType.GetProperty("Target")?.GetValueGetter()?.Invoke(formatterFunc);

                            if (null != method && target != null)
                            {
                                // apply custom formatterFunc
                                propertyValue = method.Invoke(target, new[] { entity, propertyValue });
                            }
                        }

                        row.CreateCell(propertyColumnDictionary[key].ColumnIndex).SetCellValue(propertyValue, propertyColumnDictionary[key].ColumnFormatter);
                    }
                }

                rowIndex++;
            }

            PostSheetProcess(sheet, sheetSetting, rowIndex, configuration, propertyColumnDictionary);

            return sheet;
        }

        public static ISheet DataTableToSheet<TEntity>([NotNull]ISheet sheet, DataTable dataTable, int sheetIndex) where TEntity : new()
        {
            if (null == dataTable || dataTable.Rows.Count == 0 || dataTable.Columns.Count == 0)
            {
                return sheet;
            }

            var configuration = InternalHelper.GetExcelConfigurationMapping<TEntity>();
            var propertyColumnDictionary = InternalHelper.GetPropertyColumnDictionary(configuration);

            if (propertyColumnDictionary.Keys.Count == 0)
            {
                return sheet;
            }

            var sheetSetting = GetSheetSetting(configuration.SheetSettings, sheetIndex);
            if (sheetSetting.HeaderRowIndex >= 0)
            {
                var headerRow = sheet.CreateRow(sheetSetting.HeaderRowIndex);
                for (var i = 0; i < dataTable.Columns.Count; i++)
                {
                    var col = propertyColumnDictionary.GetPropertySettingByPropertyName(dataTable.Columns[i].ColumnName);
                    if (null != col)
                    {
                        headerRow.CreateCell(col.ColumnIndex).SetCellValue(col.ColumnTitle);
                    }
                }
            }

            for (var i = 0; i < dataTable.Rows.Count; i++)
            {
                var row = sheet.CreateRow(sheetSetting.StartRowIndex + i);
                for (var j = 0; j < dataTable.Columns.Count; j++)
                {
                    var col = propertyColumnDictionary.GetPropertySettingByPropertyName(dataTable.Columns[j].ColumnName);
                    row.CreateCell(col.ColumnIndex).SetCellValue(dataTable.Rows[i][j], col.ColumnFormatter);
                }
            }

            PostSheetProcess(sheet, sheetSetting, dataTable.Rows.Count, configuration, propertyColumnDictionary);

            return sheet;
        }

        private static void PostSheetProcess<TEntity>(ISheet sheet, SheetSetting sheetSetting, int rowsCount, ExcelConfiguration<TEntity> excelConfiguration, IDictionary<PropertyInfo, PropertyConfiguration> propertyColumnDictionary)
        {
            if (rowsCount > 0)
            {
                foreach (var setting in propertyColumnDictionary.Values)
                {
                    if (setting.ColumnWidth > 0)
                    {
                        sheet.SetColumnWidth(setting.ColumnIndex, setting.ColumnWidth * 256);
                    }
                    else
                    {
                        if (sheetSetting.AutoColumnWidthEnabled)
                        {
                            sheet.AutoSizeColumn(setting.ColumnIndex);
                        }
                    }
                }

                foreach (var freezeSetting in excelConfiguration.FreezeSettings)
                {
                    sheet.CreateFreezePane(freezeSetting.ColSplit, freezeSetting.RowSplit, freezeSetting.LeftMostColumn, freezeSetting.TopRow);
                }

                if (excelConfiguration.FilterSetting != null)
                {
                    var headerIndex = sheetSetting.HeaderRowIndex >= 0 ? sheetSetting.HeaderRowIndex : 0;
                    sheet.SetAutoFilter(headerIndex, rowsCount + headerIndex, excelConfiguration.FilterSetting.FirstColumn, excelConfiguration.FilterSetting.LastColumn ?? propertyColumnDictionary.Values.Max(_ => _.ColumnIndex));
                }
            }
        }
    }
}
