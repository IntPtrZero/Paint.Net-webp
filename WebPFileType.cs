﻿////////////////////////////////////////////////////////////////////////
//
// This file is part of pdn-webp, a FileType plugin for Paint.NET
// that loads and saves WebP images.
//
// Copyright (c) 2011-2019 Nicholas Hayes
//
// This file is licensed under the MIT License.
// See LICENSE.txt for complete licensing and attribution information.
//
////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using PaintDotNet;
using PaintDotNet.IndirectUI;
using PaintDotNet.IO;
using PaintDotNet.PropertySystem;
using WebPFileType.Exif;
using WebPFileType.Properties;

namespace WebPFileType
{
    [PluginSupportInfo(typeof(PluginSupportInfo))]
    public sealed class WebPFileType : PropertyBasedFileType, IFileTypeFactory
    {
        private const string WebPColorProfile = "WebPICC";
        private const string WebPEXIF = "WebPEXIF";
        private const string WebPXMP = "WebPXMP";

        private enum PropertyNames
        {
            Preset,
            Quality,
            KeepMetadata
        }

        public WebPFileType() : base("WebP", FileTypeFlags.SupportsLoading | FileTypeFlags.SupportsSaving | FileTypeFlags.SavesWithProgress, new string[] { ".webp" })
        {
        }

        public FileType[] GetFileTypeInstances()
        {
            return new FileType[] { new WebPFileType()};
        }

        private static byte[] GetMetadataBytes(byte[] data, WebPFile.MetadataType type)
        {
            byte[] bytes = null;

            uint size = WebPFile.GetMetadataSize(data, type);
            if (size > 0)
            {
                bytes = new byte[size];
                WebPFile.ExtractMetadata(data, type, bytes, size);
            }

            return bytes;
        }

        private static PropertyItem GetAndRemoveExifValue(ref List<PropertyItem> exifMetadata, ExifTagID tag)
        {
            int tagID = unchecked((ushort)tag);

            PropertyItem value = exifMetadata.Find(p => p.Id == tagID);

            if (value != null)
            {
                exifMetadata.RemoveAll(p => p.Id == tagID);
            }

            return value;
        }

        private static Document GetOrientedDocument(byte[] bytes, out List<PropertyItem> exifMetadata)
        {
            exifMetadata = null;

            int width;
            int height;
            if (!WebPFile.WebPGetDimensions(bytes, out width, out height))
            {
                throw new WebPException(Resources.InvalidWebPImage);
            }

            Document doc = null;

            // Load the image into a Bitmap so the EXIF orientation transform can be applied.

            using (Bitmap image = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                BitmapData bitmapData = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    WebPFile.VP8StatusCode status = WebPFile.WebPLoad(bytes, bitmapData);
                    if (status != WebPFile.VP8StatusCode.Ok)
                    {
                        switch (status)
                        {
                            case WebPFile.VP8StatusCode.OutOfMemory:
                                throw new OutOfMemoryException();
                            case WebPFile.VP8StatusCode.InvalidParam:
                                throw new WebPException(Resources.InvalidParameter);
                            case WebPFile.VP8StatusCode.BitStreamError:
                            case WebPFile.VP8StatusCode.UnsupportedFeature:
                            case WebPFile.VP8StatusCode.NotEnoughData:
                                throw new WebPException(Resources.InvalidWebPImage);
                            default:
                                break;
                        }
                    }
                }
                finally
                {
                    image.UnlockBits(bitmapData);
                }

                byte[] exifBytes = GetMetadataBytes(bytes, WebPFile.MetadataType.EXIF);
                if (exifBytes != null)
                {
                    exifMetadata = ExifParser.Parse(exifBytes);

                    if (exifMetadata.Count > 0)
                    {
                        PropertyItem orientationProperty = GetAndRemoveExifValue(ref exifMetadata, ExifTagID.Orientation);
                        if (orientationProperty != null)
                        {
                            RotateFlipType transform = PropertyItemHelpers.GetOrientationTransform(orientationProperty);
                            if (transform != RotateFlipType.RotateNoneFlipNone)
                            {
                                image.RotateFlip(transform);
                            }
                        }

                        PropertyItem xResProperty = GetAndRemoveExifValue(ref exifMetadata, ExifTagID.XResolution);
                        PropertyItem yResProperty = GetAndRemoveExifValue(ref exifMetadata, ExifTagID.YResolution);
                        PropertyItem resUnitProperty = GetAndRemoveExifValue(ref exifMetadata, ExifTagID.ResolutionUnit);
                        if (xResProperty != null && yResProperty != null && resUnitProperty != null)
                        {
                            if (PropertyItemHelpers.TryDecodeRational(xResProperty, out double xRes) &&
                                PropertyItemHelpers.TryDecodeRational(yResProperty, out double yRes) &&
                                PropertyItemHelpers.TryDecodeShort(resUnitProperty, out ushort resUnit))
                            {
                                if (xRes > 0.0 && yRes > 0.0)
                                {
                                    double dpiX, dpiY;

                                    switch ((MeasurementUnit)resUnit)
                                    {
                                        case MeasurementUnit.Centimeter:
                                            dpiX = Document.DotsPerCmToDotsPerInch(xRes);
                                            dpiY = Document.DotsPerCmToDotsPerInch(yRes);
                                            break;
                                        case MeasurementUnit.Inch:
                                            dpiX = xRes;
                                            dpiY = yRes;
                                            break;
                                        default:
                                            // Unknown ResolutionUnit value.
                                            dpiX = 0.0;
                                            dpiY = 0.0;
                                            break;
                                    }

                                    if (dpiX > 0.0 && dpiY > 0.0)
                                    {
                                        try
                                        {
                                            image.SetResolution((float)dpiX, (float)dpiY);
                                        }
                                        catch
                                        {
                                            // Ignore any errors when setting the resolution.
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                doc = Document.FromGdipImage(image);
            }

            return doc;
        }

        protected override Document OnLoad(Stream input)
        {
            byte[] bytes = new byte[input.Length];

            input.ProperRead(bytes, 0, (int)input.Length);

            Document doc = GetOrientedDocument(bytes, out List<PropertyItem> exifMetadata);

            byte[] colorProfileBytes = GetMetadataBytes(bytes, WebPFile.MetadataType.ColorProfile);
            if (colorProfileBytes != null)
            {
                PropertyItem colorProfileItem = PaintDotNet.SystemLayer.PdnGraphics.CreatePropertyItem();
                colorProfileItem.Id = unchecked((ushort)ExifTagID.IccProfileData);
                colorProfileItem.Type = (short)ExifTagType.Undefined;
                colorProfileItem.Len = colorProfileBytes.Length;
                colorProfileItem.Value = colorProfileBytes.CloneT();

                doc.Metadata.AddExifValues(new PropertyItem[] { colorProfileItem });
            }

            if (exifMetadata != null)
            {
                foreach (PropertyItem item in exifMetadata)
                {
                    doc.Metadata.AddExifValues(new PropertyItem[] { item });
                }
            }

            byte[] xmpBytes = GetMetadataBytes(bytes, WebPFile.MetadataType.XMP);
            if (xmpBytes != null)
            {
                doc.Metadata.SetUserValue(WebPXMP, Convert.ToBase64String(xmpBytes, Base64FormattingOptions.None));
            }

            return doc;
        }

        public override PropertyCollection OnCreateSavePropertyCollection()
        {
            List<Property> props = new List<Property>
            {
                StaticListChoiceProperty.CreateForEnum(PropertyNames.Preset, WebPPreset.Photo, false),
                new Int32Property(PropertyNames.Quality, 95, 0, 100, false),
                new BooleanProperty(PropertyNames.KeepMetadata, true, false)
            };

            return new PropertyCollection(props);
        }

        public override ControlInfo OnCreateSaveConfigUI(PropertyCollection props)
        {
            ControlInfo info = CreateDefaultSaveConfigUI(props);

            PropertyControlInfo presetPCI = info.FindControlForPropertyName(PropertyNames.Preset);

            presetPCI.ControlProperties[ControlInfoPropertyNames.DisplayName].Value = Resources.Preset_Text;
            presetPCI.SetValueDisplayName(WebPPreset.Default, Resources.Preset_Default_Name);
            presetPCI.SetValueDisplayName(WebPPreset.Drawing, Resources.Preset_Drawing_Name);
            presetPCI.SetValueDisplayName(WebPPreset.Icon, Resources.Preset_Icon_Name);
            presetPCI.SetValueDisplayName(WebPPreset.Photo, Resources.Preset_Photo_Name);
            presetPCI.SetValueDisplayName(WebPPreset.Picture, Resources.Preset_Picture_Name);
            presetPCI.SetValueDisplayName(WebPPreset.Text, Resources.Preset_Text_Name);

            info.SetPropertyControlValue(PropertyNames.Quality, ControlInfoPropertyNames.DisplayName, Resources.Quality_Text);

            info.SetPropertyControlValue(PropertyNames.KeepMetadata, ControlInfoPropertyNames.DisplayName, string.Empty);
            info.SetPropertyControlValue(PropertyNames.KeepMetadata, ControlInfoPropertyNames.Description, Resources.KeepMetadata_Text);

            return info;
        }

        protected override bool IsReflexive(PropertyBasedSaveConfigToken token)
        {
            int quality = token.GetProperty<Int32Property>(PropertyNames.Quality).Value;

            return quality == 100;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RCS1075", Justification = "Ignore any errors thrown by SetResolution.")]
        private static void LoadProperties(Bitmap bitmap, MeasurementUnit dpuUnit, double dpuX, double dpuY, IEnumerable<PropertyItem> propertyItems)
        {

            // Sometimes GDI+ does not honor the resolution tags that we
            // put in manually via the EXIF properties.
            float dpiX;
            float dpiY;

            switch (dpuUnit)
            {
                case MeasurementUnit.Centimeter:
                    dpiX = (float)Document.DotsPerCmToDotsPerInch(dpuX);
                    dpiY = (float)Document.DotsPerCmToDotsPerInch(dpuY);
                    break;

                case MeasurementUnit.Inch:
                    dpiX = (float)dpuX;
                    dpiY = (float)dpuY;
                    break;

                default:
                case MeasurementUnit.Pixel:
                    dpiX = 1.0f;
                    dpiY = 1.0f;
                    break;
            }

            try
            {
                bitmap.SetResolution(dpiX, dpiY);
            }
            catch (Exception)
            {
                // Ignore error
            }

            foreach (PropertyItem pi in propertyItems)
            {
                try
                {
                    bitmap.SetPropertyItem(pi);
                }
                catch (ArgumentException)
                {
                    // Ignore error: the image does not support property items
                }
            }
        }

        private static List<PropertyItem> GetMetadataFromDocument(Document doc)
        {
            List<PropertyItem> items = new List<PropertyItem>();

            Metadata metadata = doc.Metadata;

            string[] exifKeys = metadata.GetKeys(Metadata.ExifSectionName);

            if (exifKeys.Length > 0)
            {
                items.Capacity = exifKeys.Length;

                foreach (string key in exifKeys)
                {
                    string blob = metadata.GetValue(Metadata.ExifSectionName, key);
                    try
                    {
                        PropertyItem pi = PaintDotNet.SystemLayer.PdnGraphics.DeserializePropertyItem(blob);

                        // GDI+ does not support the Interoperability IFD tags.
                        if (!IsInteroperabilityIFDTag(pi))
                        {
                            items.Add(pi);
                        }
                    }
                    catch
                    {
                        // Ignore any items that cannot be deserialized.
                    }
                }
            }

            return items;

            bool IsInteroperabilityIFDTag(PropertyItem propertyItem)
            {
                if (propertyItem.Id == 1)
                {
                    // The tag number 1 is used by both the GPS IFD (GPSLatitudeRef) and the Interoperability IFD (InteroperabilityIndex).
                    // The EXIF specification states that InteroperabilityIndex should be a four character ASCII field.

                    return propertyItem.Type == (short)ExifTagType.Ascii && propertyItem.Len == 4;
                }
                else if (propertyItem.Id == 2)
                {
                    // The tag number 2 is used by both the GPS IFD (GPSLatitude) and the Interoperability IFD (InteroperabilityVersion).
                    // The DCF specification states that InteroperabilityVersion should be a four byte field.
                    switch ((ExifTagType)propertyItem.Type)
                    {
                        case ExifTagType.Byte:
                        case ExifTagType.Undefined:
                            return propertyItem.Len == 4;
                        default:
                            return false;
                    }
                }
                else
                {
                    switch (propertyItem.Id)
                    {
                        case 4096: // Interoperability IFD - RelatedImageFileFormat
                        case 4097: // Interoperability IFD - RelatedImageWidth
                        case 4098: // Interoperability IFD - RelatedImageHeight
                            return true;
                        default:
                            return false;
                    }
                }
            }
        }

        private static WebPFile.MetadataParams GetMetadata(Document doc, Surface scratchSurface)
        {
            byte[] iccProfileBytes = null;
            byte[] exifBytes = null;
            byte[] xmpBytes = null;

            string colorProfile = doc.Metadata.GetUserValue(WebPColorProfile);
            if (!string.IsNullOrEmpty(colorProfile))
            {
                iccProfileBytes = Convert.FromBase64String(colorProfile);
            }

            string exif = doc.Metadata.GetUserValue(WebPEXIF);
            if (!string.IsNullOrEmpty(exif))
            {
                exifBytes = Convert.FromBase64String(exif);
            }

            string xmp = doc.Metadata.GetUserValue(WebPXMP);
            if (!string.IsNullOrEmpty(xmp))
            {
                xmpBytes = Convert.FromBase64String(xmp);
            }

            if (iccProfileBytes == null || exifBytes == null)
            {
                List<PropertyItem> propertyItems = GetMetadataFromDocument(doc);

                if (propertyItems.Count > 0)
                {
                    if (iccProfileBytes == null)
                    {
                        PropertyItem item = GetAndRemoveExifValue(ref propertyItems, ExifTagID.IccProfileData);

                        if (item != null)
                        {
                            iccProfileBytes = item.Value.CloneT();
                        }
                    }

                    if (exifBytes == null)
                    {
                        using (MemoryStream stream = new MemoryStream())
                        {
                            using (Bitmap bmp = scratchSurface.CreateAliasedBitmap())
                            {
                                LoadProperties(bmp, doc.DpuUnit, doc.DpuX, doc.DpuY, propertyItems);
                                bmp.Save(stream, ImageFormat.Jpeg);
                            }

                            exifBytes = JPEGReader.ExtractEXIF(stream);
                        }
                    }
                }
            }

            if (iccProfileBytes != null || exifBytes != null || xmpBytes != null)
            {
                return new WebPFile.MetadataParams(iccProfileBytes, exifBytes, xmpBytes);
            }

            return null;
        }

        protected override void OnSaveT(Document input, Stream output, PropertyBasedSaveConfigToken token, Surface scratchSurface, ProgressEventHandler progressCallback)
        {
            WebPFile.WebPReportProgress encProgress = new WebPFile.WebPReportProgress(delegate (int percent)
            {
                progressCallback(this, new ProgressEventArgs(percent));
            });

            int quality = token.GetProperty<Int32Property>(PropertyNames.Quality).Value;
            WebPPreset preset = (WebPPreset)token.GetProperty(PropertyNames.Preset).Value;
            bool keepMetadata = token.GetProperty<BooleanProperty>(PropertyNames.KeepMetadata).Value;

            WebPFile.EncodeParams encParams = new WebPFile.EncodeParams
            {
                quality = quality,
                preset = preset
            };

            using (RenderArgs ra = new RenderArgs(scratchSurface))
            {
                input.Render(ra, true);
            }

            WebPFile.MetadataParams metadata = null;
            if (keepMetadata)
            {
                metadata = GetMetadata(input, scratchSurface);
            }

            WebPFile.WebPSave(WriteImageCallback, scratchSurface, encParams, metadata, encProgress);

            void WriteImageCallback(IntPtr image, UIntPtr imageSize)
            {
                // 81920 is the largest multiple of 4096 that is below the large object heap threshold.
                const int MaxBufferSize = 81920;

                long size = checked((long)imageSize.ToUInt64());

                int bufferSize = (int)Math.Min(size, MaxBufferSize);

                byte[] streamBuffer = new byte[bufferSize];

                output.SetLength(size);

                long offset = 0;
                long remaining = size;

                while (remaining > 0)
                {
                    int copySize = (int)Math.Min(MaxBufferSize, remaining);

                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(image.ToInt64() + offset), streamBuffer, 0, copySize);

                    output.Write(streamBuffer, 0, copySize);

                    offset += copySize;
                    remaining -= copySize;
                }
            }
        }
    }
}
