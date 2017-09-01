﻿using Microsoft.Azure.WebJobs;
using Microsoft.WindowsAzure.Storage.Blob;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BingImageDownloader
{
    public static class MosaicBuilder
    {
        private static QuadrantMatchingTileProvider MatchingTileProvider = new QuadrantMatchingTileProvider();

        public static int TileHeight { get; set; }
        public static int TileWidth { get; set; }
        public static int DitheringRadius { get; set; }
        public static int ScaleMultiplier { get; set; }

        public class MosaicRequest
        {
            public string SourceContainer { get; set; }
            public string SourceBlob { get; set; }
            public string TileImageContainer { get; set; }
            public string TileDirectory { get; set; }
            public string OutputName { get; set; }
        }

        [FunctionName("CreateMosaic")]
        public static void CreateMosaic(
            [QueueTrigger("generate-mosaic")] MosaicRequest mosaicRequest,
            [Blob("{SourceContainer}/{SourceBlob}", FileAccess.Read)] Stream sourceImage,
            [Blob("{TileImageContainer}")] CloudBlobContainer tileContainer,
            [Blob("mosaic-output/{OutputName}", FileAccess.Write)] Stream outputStream)
        {
            MosaicBuilder.TileHeight = int.Parse(Environment.GetEnvironmentVariable("MosaicTileWidth"));
            MosaicBuilder.TileWidth = int.Parse(Environment.GetEnvironmentVariable("MosaicTileHeight"));
            MosaicBuilder.DitheringRadius = -1;
            MosaicBuilder.ScaleMultiplier = 1;

            var directory = tileContainer.GetDirectoryReference(mosaicRequest.TileDirectory);
            var blobs = directory.ListBlobs(true);
            var tileImages = new List<byte[]>();

            foreach (var b in blobs) {
                if (b.GetType() == typeof(CloudBlockBlob)) {
                    var blob = (CloudBlockBlob)b;
                    blob.FetchAttributes();

                    var bytes = new byte[blob.Properties.Length];
                    blob.DownloadToByteArray(bytes, 0);

                    tileImages.Add(bytes);
                }
            }

            MatchingTileProvider.SetSourceStream(sourceImage);

            MatchingTileProvider.ProcessInputImageColors(MosaicBuilder.TileWidth, MosaicBuilder.TileHeight);
            MatchingTileProvider.ProcessTileColors(tileImages);

            GenerateMosaic(sourceImage, tileImages, outputStream);

            // TODO: dispose bitmaps and streams
        }

        public static void SaveImage(string fullPath, SKImage outImage)
        {
            var imageBytes = outImage.Encode(SKEncodedImageFormat.Jpeg, 80);
            using (var outStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write)) {
                imageBytes.SaveTo(outStream);
            }
        }

        private static void GenerateMosaic(Stream inputStream, List<byte[]> tileImages, Stream outputStream)
        {
            SKBitmap[,] mosaicTileGrid;

            inputStream.Seek(0, SeekOrigin.Begin);

            using (var skStream = new SKManagedStream(inputStream))
            using (var bitmap = SKBitmap.Decode(skStream)) {

                // use transparency for the source image overlay
                var srcImagePaint = new SKPaint() { Color = SKColors.White.WithAlpha(200) };

                int xTileCount = bitmap.Width / MosaicBuilder.TileWidth;
                int yTileCount = bitmap.Height / MosaicBuilder.TileHeight;

                int tileCount = xTileCount * yTileCount;

                mosaicTileGrid = new SKBitmap[xTileCount, yTileCount];

                int finalTileWidth = MosaicBuilder.TileWidth * MosaicBuilder.ScaleMultiplier;
                int finalTileHeight = MosaicBuilder.TileHeight * MosaicBuilder.ScaleMultiplier;
                int targetWidth = xTileCount * finalTileWidth;
                int targetHeight = yTileCount * finalTileHeight;

                var tileList = new List<(int, int)>();

                // add coordinates for the left corner of each tile
                for (int x = 0; x < xTileCount; x++) {
                    for (int y = 0; y < yTileCount; y++) {
                        tileList.Add((x, y));
                    }
                }

                // create output surface
                var surface = SKSurface.Create(targetWidth, targetHeight, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
                surface.Canvas.DrawColor(SKColors.White); // clear the canvas / fill with white
                surface.Canvas.DrawBitmap(bitmap, 0, 0, srcImagePaint);

                // using the Darken blend mode causes colors from the source image to come through
                var tilePaint = new SKPaint() { BlendMode = SKBlendMode.Darken };
                surface.Canvas.SaveLayer(tilePaint); // save layer so blend mode is applied

                var random = new Random();

                while (tileList.Count > 0) {

                    // choose a new tile at random
                    int nextIndex = random.Next(tileList.Count);
                    var tileInfo = tileList[nextIndex];
                    tileList.RemoveAt(nextIndex);

                    // get the tile image for this point
                    //var exclusionList = GetExclusionList(mosaicTileGrid, tileInfo.Item1, tileInfo.Item2);
                    var tileBitmap = MatchingTileProvider.GetImageForTile(tileInfo.Item1, tileInfo.Item2);
                    mosaicTileGrid[tileInfo.Item1, tileInfo.Item2] = tileBitmap;

                    // draw the tile on the surface at the coordinates
                    SKRect tileRect = SKRect.Create(tileInfo.Item1 * TileWidth, tileInfo.Item2 * TileHeight, finalTileWidth, finalTileHeight);
                    surface.Canvas.DrawBitmap(tileBitmap, tileRect);
                }

                surface.Canvas.Restore(); // merge layers
                surface.Canvas.Flush();

                var imageBytes = surface.Snapshot().Encode(SKEncodedImageFormat.Jpeg, 80);
                imageBytes.SaveTo(outputStream);
            }
        }

        private static List<string> GetExclusionList(string[,] mosaicTileGrid, int xIndex, int yIndex)
        {
            int xRadius = (MosaicBuilder.DitheringRadius != -1 ? MosaicBuilder.DitheringRadius : mosaicTileGrid.GetLength(0));
            int yRadius = (MosaicBuilder.DitheringRadius != -1 ? MosaicBuilder.DitheringRadius : mosaicTileGrid.GetLength(1));

            var exclusionList = new List<string>();

            // TODO: add this back. Currently requires too many input tile images

            //for (int x = Math.Max(0, xIndex - xRadius); x < Math.Min(mosaicTileGrid.GetLength(0), xIndex + xRadius); x++) {
            //    for (int y = Math.Max(0, yIndex - yRadius); y < Math.Min(mosaicTileGrid.GetLength(1), yIndex + yRadius); y++) {
            //        if (mosaicTileGrid[x, y] != null)
            //            exclusionList.Add(mosaicTileGrid[x, y]);
            //    }
            //}

            return exclusionList;
        }
    }

    public class QuadrantMatchingTileProvider
    {
        internal static int quadrantDivisionCount = 1;
        private Stream inputStream;
        private SKColor[,][,] inputImageRGBGrid;
        private List<(SKBitmap, SKColor[,])> tileImageRGBGridList;

        public void SetSourceStream(Stream inputStream)
        {
            this.inputStream = inputStream;
        }

        // Preprocess the quadrants of the input image
        public void ProcessInputImageColors(int tileWidth, int tileHeight)
        {
            using (var skStream = new SKManagedStream(inputStream))
            using (var bitmap = SKBitmap.Decode(skStream)) {

                int xTileCount = bitmap.Width / tileWidth;
                int yTileCount = bitmap.Height / tileHeight;

                int tileDivisionWidth = tileWidth / quadrantDivisionCount;
                int tileDivisionHeight = tileHeight / quadrantDivisionCount;

                int quadrantsCompleted = 0;
                int quadrantsTotal = xTileCount * yTileCount * quadrantDivisionCount * quadrantDivisionCount;
                inputImageRGBGrid = new SKColor[xTileCount, yTileCount][,];

                //Divide the input image into separate tile sections and calculate the average pixel value for each one
                for (int yTileIndex = 0; yTileIndex < yTileCount; yTileIndex++) {
                    for (int xTileIndex = 0; xTileIndex < xTileCount; xTileIndex++) {
                        var rect = SKRectI.Create(xTileIndex * tileWidth, yTileIndex * tileHeight, tileWidth, tileHeight);
                        inputImageRGBGrid[xTileIndex, yTileIndex] = GetAverageColorGrid(bitmap, rect);
                        quadrantsCompleted += (quadrantDivisionCount * quadrantDivisionCount);
                    }
                }
            }
        }

        // Convert tile images to average color
        public void ProcessTileColors(List<byte[]> tileImages)
        {
            tileImageRGBGridList = new List<(SKBitmap, SKColor[,])>();

            foreach (var bytes in tileImages) {

                var bitmap = SKBitmap.Decode(bytes);

                var rect = SKRectI.Create(0, 0, bitmap.Width, bitmap.Height);
                tileImageRGBGridList.Add((bitmap, GetAverageColorGrid(bitmap, rect)));
            }
        }

        // Returns the best match image per tile area
        public SKBitmap GetImageForTile(int xIndex, int yIndex)
        {
            var tileDistances = new List<(double, SKBitmap)>();

            foreach (var tileGrid in tileImageRGBGridList) {
                double distance = 0;

                for (int x = 0; x < quadrantDivisionCount; x++)
                    for (int y = 0; y < quadrantDivisionCount; y++) {
                        distance +=
                            Math.Sqrt(
                                Math.Abs(Math.Pow(tileGrid.Item2[x, y].Red, 2) - Math.Pow(inputImageRGBGrid[xIndex, yIndex][x, y].Red, 2)) +
                                Math.Abs(Math.Pow(tileGrid.Item2[x, y].Green, 2) - Math.Pow(inputImageRGBGrid[xIndex, yIndex][x, y].Green, 2)) +
                                Math.Abs(Math.Pow(tileGrid.Item2[x, y].Blue, 2) - Math.Pow(inputImageRGBGrid[xIndex, yIndex][x, y].Blue, 2)));
                    }

                tileDistances.Add((distance, tileGrid.Item1));
            }

            var sorted = tileDistances
                //.Where(x => !excludedImageFiles.Contains(x.Item2)) // remove items from excluded list
                .OrderBy(item => item.Item1); // sort by best match

            return sorted.First().Item2;
        }

        // Converts a portion of the base image to an average RGB color
        private SKColor[,] GetAverageColorGrid(SKBitmap bitmap, SKRectI bounds)
        {
            var rgbGrid = new SKColor[quadrantDivisionCount, quadrantDivisionCount];
            int xDivisionSize = bounds.Width / quadrantDivisionCount;
            int yDivisionSize = bounds.Height / quadrantDivisionCount;

            for (int yDivisionIndex = 0; yDivisionIndex < quadrantDivisionCount; yDivisionIndex++) {
                for (int xDivisionIndex = 0; xDivisionIndex < quadrantDivisionCount; xDivisionIndex++) {

                    int pixelCount = 0;
                    int totalR = 0, totalG = 0, totalB = 0;

                    for (int y = yDivisionIndex * yDivisionSize; y < (yDivisionIndex + 1) * yDivisionSize; y++) {
                        for (int x = xDivisionIndex * xDivisionSize; x < (xDivisionIndex + 1) * xDivisionSize; x++) {

                            var pixel = bitmap.GetPixel(x + bounds.Left, y + bounds.Top);

                            totalR += pixel.Red;
                            totalG += pixel.Green;
                            totalB += pixel.Blue;
                            pixelCount++;
                        }
                    }

                    var finalR = (byte)(totalR / pixelCount);
                    var finalG = (byte)(totalG / pixelCount);
                    var finalB = (byte)(totalB / pixelCount);

                    rgbGrid[xDivisionIndex, yDivisionIndex] = new SKColor(finalR, finalG, finalB);
                }
            }

            return rgbGrid;
        }
    }
}

