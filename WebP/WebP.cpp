#include <stdio.h>
#include <stdlib.h>
#include <memory.h>
#include "WebP.h"

bool __stdcall WebPGetDimensions(const uint8_t* iData, size_t iDataSize, int* oWidth, int* oHeight)
{
	return (WebPGetInfo(iData, iDataSize, oWidth, oHeight) != 0);
}

int __stdcall WebPLoad(const uint8_t* data, size_t dataSize, uint8_t* outData, uint32_t outSize, int outStride)
{
	WebPDecoderConfig config;
	WebPDecBuffer* const output_buffer = &config.output;

	if (!WebPInitDecoderConfig(&config))
	{
		return errVersionMismatch;
	}
	
	output_buffer->colorspace = MODE_BGRA;
	output_buffer->is_external_memory = 1;
	output_buffer->u.RGBA.rgba = outData;
	output_buffer->u.RGBA.size = outSize;
	output_buffer->u.RGBA.stride = outStride;

	
	return WebPDecode(data, dataSize, &config);
}

static bool HasTransparency(const void* data, int width, int height, int stride)
{
	const uint8_t* scan0 = reinterpret_cast<const uint8_t*>(data);
	
	for (int y = 0; y < height; y++)
	{
		const uint8_t* ptr = scan0 + (y * stride); 
		for (int x = 0; x < width; x++)
		{
			if (ptr[3] < 255)
				return true;

			ptr += 4;
		}
	}

	return false;
}

static int progressFunc(int percent, const WebPPicture* picture)
{
	ProgressFn callback = reinterpret_cast<ProgressFn>(picture->user_data);
	callback(percent);

	return 1;
}

int __stdcall WebPSave(
	void** output,
	const OutputBufferAllocFn outputAllocator,
	const void* bitmap,
	const int width,
	const int height,
	const int stride,
	const EncodeParams* params,
	ProgressFn callback)
{
	if (outputAllocator == nullptr)
	{
		return VP8_ENC_ERROR_NULL_PARAMETER;
	}

	WebPConfig config;
	WebPPicture pic;
	WebPMemoryWriter wrt;

	if (!WebPConfigPreset(&config, static_cast<WebPPreset>(params->preset), params->quality) || !WebPPictureInit(&pic))
	{
		return errVersionMismatch; // WebP API version mismatch
	}

	config.method = params->method;
	config.thread_level = 1;

	if (params->quality == 100)
	{
		config.lossless = 1;
		pic.use_argb = 1;

		switch (params->preset)
		{
		case WEBP_PRESET_PHOTO:
			config.image_hint = WEBP_HINT_PHOTO; 
			break;
		case WEBP_PRESET_PICTURE:
			config.image_hint = WEBP_HINT_PICTURE; 
			break;
		case WEBP_PRESET_DRAWING:
			config.image_hint = WEBP_HINT_GRAPH; 
			break;
		}
	}
	else
	{
		if (params->fileSize > 0)
		{
			config.target_size = params->fileSize;
		}

		if (params->preset < WEBP_PRESET_ICON)
		{
			config.filter_strength = params->filterStrength;
		}
		config.filter_type = params->filterType;
		config.filter_sharpness = params->sharpness;
		config.sns_strength = params->noiseShaping;
	}

	pic.width = width;
	pic.height = height;

	WebPMemoryWriterInit(&wrt);
	pic.writer = WebPMemoryWrite;
	pic.custom_ptr = &wrt;
		
	if (HasTransparency(bitmap, width, height, stride))
	{
		if (WebPPictureImportBGRA(&pic, reinterpret_cast<const uint8_t*>(bitmap), stride) == 0)
		{
			return VP8_ENC_ERROR_OUT_OF_MEMORY; 
		}
	}
	else
	{
		// If the image does not have any transparency import using the BGRX method which will ignore the alpha channel.
		if (WebPPictureImportBGRX(&pic, reinterpret_cast<const uint8_t*>(bitmap), stride) == 0)
		{
			return VP8_ENC_ERROR_OUT_OF_MEMORY;
		}
	}

	if (callback != nullptr)
	{
		pic.user_data = callback;
		pic.progress_hook = progressFunc;
	}

	int error = 0;
	if (WebPEncode(&config, &pic) != 0) // C-style Boolean
	{
		*output = outputAllocator(wrt.size);
		if (*output != nullptr)
		{
			memcpy_s(*output, wrt.size, wrt.mem, wrt.size);
		}
		else
		{
			error = VP8_ENC_ERROR_OUT_OF_MEMORY;
		}
	}
	else
	{	
		error = static_cast<int>(pic.error_code);
	}
	WebPMemoryWriterClear(&wrt);
	WebPPictureFree(&pic); // free the allocated memory and return the error code if necessary.

	return error;
}

void __stdcall GetMetaDataSize(const uint8_t* data, size_t dataSize, MetaDataType type, uint32_t* outSize)
{
	*outSize = 0;

	WebPData webpData;
	webpData.bytes = data;
	webpData.size = dataSize;

	WebPDemuxer* demux = WebPDemux(&webpData);
	if (demux != nullptr)
	{
		uint32_t flags = WebPDemuxGetI(demux, WEBP_FF_FORMAT_FLAGS);

		WebPChunkIterator iter;
		memset(&iter, 0, sizeof(WebPChunkIterator));

		switch (type)
		{
		case ColorProfile:
			if ((flags & ICCP_FLAG) != 0)
			{
				WebPDemuxGetChunk(demux, "ICCP", 1, &iter);
			}
		break;
		case EXIF:
			if ((flags & EXIF_FLAG) != 0)
			{
				WebPDemuxGetChunk(demux, "EXIF", 1, &iter);
			}
			break;
		case XMP:
			if ((flags & XMP_FLAG) != 0)
			{
				WebPDemuxGetChunk(demux, "XMP ", 1, &iter);
			}
			break;
		}

		*outSize = static_cast<uint32_t>(iter.chunk.size);

		WebPDemuxReleaseChunkIterator(&iter);
		WebPDemuxDelete(demux);
	}
	
}

void __stdcall ExtractMetaData(const uint8_t* data, size_t dataSize, uint8_t* outData, uint32_t outSize, MetaDataType type)
{
	WebPData webpData;
	webpData.bytes = data;
	webpData.size = dataSize;

	WebPDemuxer* demux = WebPDemux(&webpData);
	if (demux != nullptr)
	{
		uint32_t flags = WebPDemuxGetI(demux, WEBP_FF_FORMAT_FLAGS);

		WebPChunkIterator iter;
		memset(&iter, 0, sizeof(WebPChunkIterator));

		switch (type)
		{
		case ColorProfile:
			if ((flags & ICCP_FLAG) != 0)
			{
				WebPDemuxGetChunk(demux, "ICCP", 1, &iter);
			}
		break;
		case EXIF:
			if ((flags & EXIF_FLAG) != 0)
			{
				WebPDemuxGetChunk(demux, "EXIF", 1, &iter);
			}
			break;
		case XMP:
			if ((flags & XMP_FLAG) != 0)
			{
				WebPDemuxGetChunk(demux, "XMP ", 1, &iter);
			}
			break;
		}

		memcpy_s(outData, outSize, iter.chunk.bytes, outSize);

		WebPDemuxReleaseChunkIterator(&iter);
		WebPDemuxDelete(demux);
	}
}

int __stdcall SetMetaData(const uint8_t* image, size_t imageSize, void** outImage, OutputBufferAllocFn outputAllocator, const MetaDataParams* metaData)
{
	if (outputAllocator == nullptr)
	{
		return WEBP_MUX_INVALID_ARGUMENT;
	}

	WebPMux* mux = WebPMuxNew();
	if (mux == nullptr)
	{
		return WEBP_MUX_MEMORY_ERROR;
	}

	WebPData imageData;
	imageData.bytes = image;
	imageData.size = imageSize;
	
	WebPMuxError error = WebPMuxSetImage(mux, &imageData, 0);

	if (error == WEBP_MUX_OK)
	{
		WebPData chunkData;

		if (metaData->iccProfileSize > 0)
		{
			chunkData.bytes = metaData->iccProfile;
			chunkData.size = metaData->iccProfileSize;
			
			error = WebPMuxSetChunk(mux, "ICCP", &chunkData, 1);
		}
		
		if (error == WEBP_MUX_OK && metaData->exifSize > 0)
		{
			chunkData.bytes = metaData->exif;
			chunkData.size = metaData->exifSize;
			
			error = WebPMuxSetChunk(mux, "EXIF", &chunkData, 1);
		}
		
		if (error == WEBP_MUX_OK && metaData->xmpSize > 0)
		{
			chunkData.bytes = metaData->xmp;
			chunkData.size = metaData->xmpSize;
			
			error = WebPMuxSetChunk(mux, "XMP ", &chunkData, 1);
		}

		if (error == WEBP_MUX_OK)
		{
			WebPData assembledData;

			error = WebPMuxAssemble(mux, &assembledData);
			WebPMuxDelete(mux);
			mux = nullptr;

			if (error == WEBP_MUX_OK)
			{
				*outImage = outputAllocator(assembledData.size);

				if (*outImage == nullptr)
				{
					error = WEBP_MUX_MEMORY_ERROR;
				}

				if (error == WEBP_MUX_OK)
				{
					memcpy_s(*outImage, assembledData.size, assembledData.bytes, assembledData.size);
				}

				WebPDataClear(&assembledData);
			}
		}

	}

	return error;
}