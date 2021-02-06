﻿////////////////////////////////////////////////////////////////////////
//
// This file is part of pdn-webp, a FileType plugin for Paint.NET
// that loads and saves WebP images.
//
// Copyright (c) 2011-2021 Nicholas Hayes
//
// This file is licensed under the MIT License.
// See LICENSE.txt for complete licensing and attribution information.
//
////////////////////////////////////////////////////////////////////////

using PaintDotNet;

namespace WebPFileType
{
    public sealed class WebPFileTypeFactory :
#if PDN_3_5_X
        IFileTypeFactory
#else
        IFileTypeFactory2
#endif
    {

#if PDN_3_5_X
        public FileType[] GetFileTypeInstances()
        {
            return new FileType[] { new WebPFileType()};
        }
#else
        public FileType[] GetFileTypeInstances(IFileTypeHost host)
        {
            return new FileType[] { new WebPFileType(host) };
        }
#endif
    }
}
