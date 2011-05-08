﻿#region License Information (GPL v2)

/*
    ZScreen - A program that allows you to upload screenshots in one keystroke.
    Copyright (C) 2008-2011 ZScreen Developers

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v2)

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Crop;
using GraphicsMgrLib;
using HelpersLib;
using Microsoft.WindowsAPICodePack.Taskbar;
using UploadersLib;
using UploadersLib.FileUploaders;
using UploadersLib.HelperClasses;
using UploadersLib.ImageUploaders;
using UploadersLib.TextUploaders;
using ZScreenLib.Properties;
using ZScreenLib.Shapes;
using ZUploader.HelperClasses;

namespace ZScreenLib
{
    public class TaskManager
    {
        public static bool mSetHotkeys, mTakingScreenShot, bAutoScreenshotsOpened, bDropWindowOpened, bQuickActionsOpened, bQuickOptionsOpened;
        private WorkerTask mTask;

        public TaskManager(WorkerTask task)
        {
            mTask = task;
        }

        #region Image Tasks Manager

        public void CaptureActiveWindow()
        {
            try
            {
                mTask.CaptureActiveWindow();
                WriteImage();
                PublishData();
            }
            catch (ArgumentOutOfRangeException aor)
            {
                mTask.Errors.Add("Invalid FTP Account Selection");
                FileSystem.AppendDebug(aor.ToString());
            }
            catch (Exception ex)
            {
                FileSystem.AppendDebug("Error while capturing active window", ex);
                if (Engine.conf.CaptureEntireScreenOnError)
                {
                    CaptureRegionOrWindow();
                }
            }
        }

        public string CaptureRegionOrWindow()
        {
            mTakingScreenShot = true;
            string filePath = string.Empty;

            bool windowMode = mTask.Job2 == WorkerTask.JobLevel2.TakeScreenshotWindowSelected;

            try
            {
                using (Image imgSS = Capture.CaptureScreen(Engine.conf.ShowCursor))
                {
                    if (mTask.Job2 == WorkerTask.JobLevel2.TAKE_SCREENSHOT_LAST_CROPPED && !Engine.LastRegion.IsEmpty)
                    {
                        mTask.SetImage(GraphicsMgr.CropImage(imgSS, Engine.LastRegion));
                    }
                    else
                    {
                        if (Engine.conf.UseCropBeta && !windowMode)
                        {
                            using (Crop2 crop = new Crop2(imgSS))
                            {
                                if (crop.ShowDialog() == DialogResult.OK)
                                {
                                    mTask.SetImage(crop.GetCroppedScreenshot());
                                }
                            }
                        }
                        else if (Engine.conf.UseCropLight && !windowMode)
                        {
                            using (CropLight crop = new CropLight(imgSS))
                            {
                                if (crop.ShowDialog() == DialogResult.OK)
                                {
                                    mTask.SetImage(GraphicsMgr.CropImage(imgSS, crop.SelectionRectangle));
                                }
                            }
                        }
                        else
                        {
                            using (Crop c = new Crop(imgSS, windowMode))
                            {
                                if (c.ShowDialog() == DialogResult.OK)
                                {
                                    if (mTask.Job2 == WorkerTask.JobLevel2.TakeScreenshotCropped && !Engine.LastRegion.IsEmpty)
                                    {
                                        mTask.SetImage(GraphicsMgr.CropImage(imgSS, Engine.LastRegion));
                                    }
                                    else if (windowMode && !Engine.LastCapture.IsEmpty)
                                    {
                                        mTask.SetImage(GraphicsMgr.CropImage(imgSS, Engine.LastCapture));
                                    }
                                }
                            }
                        }
                    }
                }

                mTakingScreenShot = false;

                if (mTask.MyImage != null)
                {
                    bool roundedShadowCorners = false;
                    if (windowMode && Engine.conf.SelectedWindowRoundedCorners || !windowMode && Engine.conf.CropShotRoundedCorners)
                    {
                        mTask.SetImage(GraphicsMgr.RemoveCorners(mTask.MyImage, null));
                        roundedShadowCorners = true;
                    }
                    if (windowMode && Engine.conf.SelectedWindowShadow || !windowMode && Engine.conf.CropShotShadow)
                    {
                        mTask.SetImage(GraphicsMgr.AddBorderShadow(mTask.MyImage, roundedShadowCorners));
                    }

                    WriteImage();
                    PublishData();
                }
            }
            catch (Exception ex)
            {
                FileSystem.AppendDebug("Error while capturing region", ex);
                mTask.Errors.Add(ex.Message);
                if (Engine.conf.CaptureEntireScreenOnError)
                {
                    CaptureScreen();
                }
            }
            finally
            {
                mTask.MyWorker.ReportProgress((int)WorkerTask.ProgressType.UpdateCropMode);
                mTakingScreenShot = false;
            }

            return filePath;
        }

        public void CaptureScreen()
        {
            mTask.CaptureScreen();
            WriteImage();
            PublishData();
        }

        public void CaptureFreehandCrop()
        {
            using (FreehandCapture crop = new FreehandCapture())
            {
                if (crop.ShowDialog() == DialogResult.OK)
                {
                    using (Image ss = Capture.CaptureScreen(false))
                    {
                        mTask.SetImage(crop.GetScreenshot(ss));
                    }
                }
                else
                {
                    mTask.RetryPending = true;
                }
            }

            if (mTask.MyImage != null)
            {
                WriteImage();
                PublishData();
            }
        }

        /// <summary>
        /// Writes MyImage object in a WorkerTask into a file
        /// </summary>
        /// <param name="t">WorkerTask</param>
        public void WriteImage()
        {
            if (mTask.MyImage != null)
            {
                NameParserType type;
                string pattern = string.Empty;
                if (mTask.Job2 == WorkerTask.JobLevel2.TAKE_SCREENSHOT_WINDOW_ACTIVE)
                {
                    type = NameParserType.ActiveWindow;
                    pattern = Engine.conf.ActiveWindowPattern;
                }
                else
                {
                    type = NameParserType.EntireScreen;
                    pattern = Engine.conf.EntireScreenPattern;
                }

                using (NameParser parser = new NameParser(type) { AutoIncrementNumber = Engine.conf.AutoIncrement })
                {
                    if (mTask.SetFilePathFromPattern(parser.Convert(pattern)))
                    {
                        Engine.conf.AutoIncrement = parser.AutoIncrementNumber;
                        FileSystem.SaveImage(ref mTask);
                        if (!File.Exists(mTask.LocalFilePath))
                        {
                            mTask.Errors.Add(string.Format("{0} does not exist", mTask.LocalFilePath));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Function to edit Image (Screenshot or Picture) in an Image Editor and Upload
        /// </summary>
        /// <param name="task"></param>
        public void PublishData()
        {
            if (mTask.Job1 == JobLevel1.File)
            {
                UploadFile();
            }
            else
            {
                PublishImage();
            }
        }

        #endregion Image Tasks Manager

        public void PublishImage()
        {
            if (mTask.MyImage != null && Adapter.ImageSoftwareEnabled() && mTask.Job2 != WorkerTask.JobLevel2.UPLOAD_IMAGE)
            {
                PerformActions(mTask);
            }

            if (mTask.MyImageUploader == ImageUploaderType.FileUploader)
            {
                UploadFile();
            }
            else
            {
                UploadImage();
            }
        }

        public void UploadFile()
        {
            mTask.StartTime = DateTime.Now;
            FileSystem.AppendDebug("Uploading File: " + mTask.LocalFilePath);

            FileUploader fileHost = null;
            switch (mTask.MyFileUploader)
            {
                case FileUploaderType.FTP:
                    switch (mTask.Job1)
                    {
                        case JobLevel1.Text:
                            UploadFTP(Engine.conf.FtpText);
                            break;
                        case JobLevel1.Image:
                            UploadFTP(Engine.conf.FtpImages);
                            break;
                        default:
                            UploadFTP(Engine.conf.FtpFiles);
                            break;
                    }
                    break;
                case FileUploaderType.SendSpace:
                    fileHost = new SendSpace(Engine.SendSpaceKey);
                    switch (Engine.conf.SendSpaceAccountType)
                    {
                        case AccountType.Anonymous:
                            SendSpaceManager.PrepareUploadInfo(Engine.SendSpaceKey, null, null);
                            break;
                        case AccountType.User:
                            SendSpaceManager.PrepareUploadInfo(Engine.SendSpaceKey, Engine.conf.SendSpaceUserName, Engine.conf.SendSpacePassword);
                            break;
                    }
                    break;
                case FileUploaderType.RapidShare:
                    fileHost = new RapidShare(new RapidShareOptions()
                    {
                        AccountType = Engine.conf.RapidShareAccountType,
                        PremiumUsername = Engine.conf.RapidSharePremiumUserName,
                        Password = Engine.conf.RapidSharePassword,
                        CollectorsID = Engine.conf.RapidShareCollectorsID
                    });
                    break;
                case FileUploaderType.Dropbox:
                    OAuthInfo oauth = new OAuthInfo(Engine.DropboxConsumerKey, Engine.DropboxConsumerSecret, Engine.conf.DropboxUserToken, Engine.conf.DropboxUserSecret);
                    string uploadPath = new NameParser { IsFolderPath = true }.Convert(Dropbox.TidyUploadPath(Engine.conf.DropboxUploadPath));
                    fileHost = new Dropbox(oauth, uploadPath, Engine.conf.DropboxUserID);
                    break;
                /*case FileUploaderType.FileBin:
                    fileHost = new FileBin();
                    break;
                case FileUploaderType.DropIO:
                    fileHost = new DropIO();
                    break;
                case FileUploaderType.FilezFiles:
                    fileHost = new FilezFiles();
                    break;*/
                case FileUploaderType.ShareCX:
                    fileHost = new ShareCX();
                    break;
                case FileUploaderType.CustomUploader:
                    if (Adapter.CheckList(Engine.conf.CustomUploadersList, Engine.conf.CustomUploaderSelected))
                    {
                        fileHost = new CustomUploader(Engine.conf.CustomUploadersList[Engine.conf.CustomUploaderSelected]);
                    }
                    break;
            }

            if (fileHost != null)
            {
                mTask.MyWorker.ReportProgress((int)WorkerTask.ProgressType.UPDATE_PROGRESS_MAX, TaskbarProgressBarState.Indeterminate);
                mTask.DestinationName = fileHost.Name;
                fileHost.ProgressChanged += UploadProgressChanged;
                UploadResult ur = fileHost.Upload(mTask.LocalFilePath);
                if (ur != null)
                {
                    mTask.UpdateRemoteFilePath(ur);
                }
                mTask.Errors = fileHost.Errors;
            }

            mTask.EndTime = DateTime.Now;
        }

        public void UploadImage()
        {
            mTask.StartTime = DateTime.Now;
            FileSystem.AppendDebug("Uploading Image: " + mTask.LocalFilePath);

            ImageUploader imageUploader = null;

            if (Engine.conf.TinyPicSizeCheck && mTask.MyImageUploader == ImageUploaderType.TINYPIC && File.Exists(mTask.LocalFilePath))
            {
                SizeF size = Image.FromFile(mTask.LocalFilePath).PhysicalDimension;
                if (size.Width > 1600 || size.Height > 1600)
                {
                    FileSystem.AppendDebug("Changing from TinyPic to ImageShack due to large image size");
                    mTask.MyImageUploader = ImageUploaderType.IMAGESHACK;
                }
            }

            switch (mTask.MyImageUploader)
            {
                case ImageUploaderType.DEKIWIKI:
                    UploadDekiWiki();
                    break;
                case ImageUploaderType.FILE:
                    string fp = mTask.LocalFilePath;
                    if (Engine.Portable)
                    {
                        fp = Path.Combine(Application.StartupPath, fp);
                        mTask.UpdateLocalFilePath(fp);
                    }
                    break;
                case ImageUploaderType.FLICKR:
                    imageUploader = new FlickrUploader(Engine.FlickrKey, Engine.FlickrSecret, Engine.conf.FlickrAuthInfo, Engine.conf.FlickrSettings);
                    break;
                /*case ImageUploaderType.IMAGEBAM:
                    ImageBamUploaderOptions imageBamOptions = new ImageBamUploaderOptions(Engine.conf.ImageBamApiKey, Engine.conf.ImageBamSecret,
                        Adapter.GetImageBamGalleryActive()) { NSFW = Engine.conf.ImageBamContentNSFW };
                    imageUploader = new ImageBamUploader(Engine.ImageBamKey, Engine.ImageBamSecret, imageBamOptions);
                    break;
                case ImageDestType.IMAGEBIN:
                    imageUploader = new ImageBin();
                    break;*/
                case ImageUploaderType.IMAGESHACK:
                    imageUploader = new ImageShackUploader(Engine.ImageShackKey, Engine.conf.ImageShackRegistrationCode);
                    ((ImageShackUploader)imageUploader).Public = Engine.conf.ImageShackShowImagesInPublic;
                    break;
                /*case ImageDestType.IMG1:
                    imageUploader = new Img1Uploader();
                    break;*/
                case ImageUploaderType.IMGUR:
                    imageUploader = new Imgur(Engine.conf.ImgurAccountType, Engine.ImgurAnonymousKey, Engine.conf.ImgurOAuthInfo);
                    break;
                case ImageUploaderType.UPLOADSCREENSHOT:
                    imageUploader = new UploadScreenshot(Engine.UploadScreenshotKey);
                    break;
                case ImageUploaderType.Localhost:
                    UploadLocalhost();
                    break;
                case ImageUploaderType.MEDIAWIKI:
                    UploadMediaWiki();
                    break;
                case ImageUploaderType.PRINTER:
                    if (mTask.MyImage != null)
                    {
                        mTask.MyWorker.ReportProgress(101, (Image)mTask.MyImage.Clone());
                    }
                    break;
                case ImageUploaderType.TINYPIC:
                    imageUploader = new TinyPicUploader(Engine.TinyPicID, Engine.TinyPicKey, Engine.conf.TinyPicShuk);
                    break;
                case ImageUploaderType.TWITPIC:
                    TwitPicOptions twitpicOpt = new TwitPicOptions();
                    twitpicOpt.Username = Engine.conf.TwitterUsername;
                    twitpicOpt.Password = Engine.conf.TwitterPassword;
                    // twitpicOpt.TwitPicUploadType = Engine.conf.TwitPicUploadMode;
                    twitpicOpt.TwitPicThumbnailMode = Engine.conf.TwitPicThumbnailMode;
                    twitpicOpt.ShowFull = Engine.conf.TwitPicShowFull;
                    imageUploader = new TwitPicUploader(twitpicOpt);
                    break;
                case ImageUploaderType.TWITSNAPS:
                    imageUploader = new TwitSnapsUploader(Engine.TwitsnapsKey, Adapter.TwitterGetActiveAcct());
                    break;
                case ImageUploaderType.YFROG:
                    YfrogOptions yfrogOp = new YfrogOptions(Engine.ImageShackKey);
                    yfrogOp.Username = Engine.conf.TwitterUsername;
                    yfrogOp.Password = Engine.conf.TwitterPassword;
                    yfrogOp.Source = Application.ProductName;
                    // yfrogOp.UploadType = Engine.conf.YfrogUploadMode;
                    imageUploader = new YfrogUploader(yfrogOp);
                    break;
            }

            //imageUploader.ProgressChanged += new ImageUploader.ProgressEventHandler(UploadProgressChanged);

            if (imageUploader != null)
            {
                imageUploader.ProgressChanged += (x) => UploadProgressChanged(x);
                FileSystem.AppendDebug("Initialized " + imageUploader.Name);
                mTask.DestinationName = imageUploader.Name;
                string fullFilePath = mTask.LocalFilePath;
                if (File.Exists(fullFilePath) || mTask.MyImage != null)
                {
                    for (int i = 0; i <= (int)Engine.conf.ErrorRetryCount; i++)
                    {
                        if (File.Exists(fullFilePath))
                        {
                            mTask.UpdateRemoteFilePath(imageUploader.Upload(fullFilePath));
                        }
                        else if (mTask.MyImage != null && mTask.FileName != null)
                        {
                            mTask.UpdateRemoteFilePath(imageUploader.Upload(mTask.MyImage, mTask.FileName.ToString()));
                        }

                        mTask.Errors = imageUploader.Errors;

                        if (string.IsNullOrEmpty(mTask.LinkManager.UploadResult.URL))
                        {
                            mTask.MyWorker.ReportProgress((int)ZScreenLib.WorkerTask.ProgressType.ShowTrayWarning, string.Format("Retrying... Attempt {1}", mTask.MyImageUploader.GetDescription(), i));
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            mTask.EndTime = DateTime.Now;

            if (Engine.conf.ImageUploadRetryOnTimeout && mTask.UploadDuration > (int)Engine.conf.UploadDurationLimit)
            {
                if (mTask.MyImageUploader == ImageUploaderType.IMAGESHACK)
                {
                    Engine.conf.MyImageUploader = (int)ImageUploaderType.TINYPIC;
                }
                else if (mTask.MyImageUploader == ImageUploaderType.TINYPIC)
                {
                    Engine.conf.MyImageUploader = (int)ImageUploaderType.IMAGESHACK;
                }
                mTask.MyWorker.ReportProgress((int)WorkerTask.ProgressType.CHANGE_UPLOAD_DESTINATION);
            }

            if (mTask.LinkManager != null)
            {
                FlashIcon(mTask);
            }
        }

        private void FlashIcon(WorkerTask t)
        {
            for (int i = 0; i < (int)Engine.conf.FlashTrayCount; i++)
            {
                t.MyWorker.ReportProgress((int)WorkerTask.ProgressType.FLASH_ICON, Resources.zss_uploaded);
                Thread.Sleep(250);
                t.MyWorker.ReportProgress((int)WorkerTask.ProgressType.FLASH_ICON, Resources.zss_green);
                Thread.Sleep(250);
            }
            t.MyWorker.ReportProgress((int)WorkerTask.ProgressType.FLASH_ICON, Resources.zss_tray);
        }

        public void UploadLocalhost()
        {
            if (Adapter.CheckList(Engine.conf.LocalhostAccountList, Engine.conf.LocalhostSelected) && File.Exists(mTask.LocalFilePath))
            {
                LocalhostAccount acc = Engine.conf.LocalhostAccountList[Engine.conf.LocalhostSelected];
                string fn = Path.GetFileName(mTask.LocalFilePath);
                string destFile = acc.GetLocalhostPath(fn);
                string destDir = Path.GetDirectoryName(destFile);
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
                File.Move(mTask.LocalFilePath, destFile);
                mTask.UpdateLocalFilePath(destFile);
                mTask.LinkManager.UploadResult.URL = acc.GetUriPath(fn);
            }
        }

        /// <summary>
        /// Funtion to FTP the Screenshot
        /// </summary>
        /// <returns>Retuns a List of Screenshots</returns>
        public bool UploadFTP(int FtpAccountId)
        {
            try
            {
                mTask.MyWorker.ReportProgress((int)WorkerTask.ProgressType.UPDATE_PROGRESS_MAX, TaskbarProgressBarState.Indeterminate);

                if (Adapter.CheckFTPAccounts(ref mTask) && File.Exists(mTask.LocalFilePath))
                {
                    FTPAccount acc = Engine.conf.FTPAccountList[FtpAccountId];
                    mTask.DestinationName = string.Format("FTP - {0}", acc.Name);
                    FileSystem.AppendDebug(string.Format("Uploading {0} to FTP: {1}", mTask.FileName, acc.Host));

                    FTPUploader fu = new FTPUploader(acc);
                    fu.ProgressChanged += new Uploader.ProgressEventHandler(UploadProgressChanged);

                    mTask.MyWorker.ReportProgress((int)WorkerTask.ProgressType.UPDATE_PROGRESS_MAX, TaskbarProgressBarState.Normal);

                    string url = fu.Upload(mTask.LocalFilePath).URL;

                    if (!string.IsNullOrEmpty(url))
                    {
                        mTask.UpdateRemoteFilePath(new UploadResult() { URL = url });

                        if (CreateThumbnail())
                        {
                            double thar = (double)Engine.conf.FTPThumbnailWidth / (double)mTask.MyImage.Width;
                            using (Image img = GraphicsMgr.ChangeImageSize(mTask.MyImage, Engine.conf.FTPThumbnailWidth, (int)(thar * mTask.MyImage.Height)))
                            {
                                StringBuilder sb = new StringBuilder(Path.GetFileNameWithoutExtension(mTask.LocalFilePath));
                                sb.Append(".th");
                                sb.Append(Path.GetExtension(mTask.LocalFilePath));
                                string thPath = Path.Combine(Path.GetDirectoryName(mTask.LocalFilePath), sb.ToString());
                                img.Save(thPath);
                                if (File.Exists(thPath))
                                {
                                    string thumb = fu.Upload(thPath).URL;

                                    if (!string.IsNullOrEmpty(thumb))
                                    {
                                        mTask.LinkManager.UploadResult.ThumbnailURL = thumb;
                                    }
                                }
                            }
                        }

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                FileSystem.AppendDebug("Error while uploading to FTP Server", ex);
                mTask.Errors.Add("FTP upload failed.\r\n" + ex.Message);
            }

            return false;
        }

        private bool CreateThumbnail()
        {
            return GraphicsMgr.IsValidImage(mTask.LocalFilePath) && mTask.MyImage != null &&
                ((ClipboardUriType)Engine.conf.MyClipboardUriMode == ClipboardUriType.LINKED_THUMBNAIL ||
                 (ClipboardUriType)Engine.conf.MyClipboardUriMode == ClipboardUriType.LINKED_THUMBNAIL_WIKI ||
                 (ClipboardUriType)Engine.conf.MyClipboardUriMode == ClipboardUriType.LinkedThumbnailHtml ||
                 (ClipboardUriType)Engine.conf.MyClipboardUriMode == ClipboardUriType.THUMBNAIL) &&
                (!Engine.conf.FTPThumbnailCheckSize || (Engine.conf.FTPThumbnailCheckSize && (mTask.MyImage.Width > Engine.conf.FTPThumbnailWidth)));
        }

        private void UploadProgressChanged(ProgressManager progress)
        {
            if (Engine.conf.ShowTrayUploadProgress)
            {
                UploadInfo uploadInfo = ClipboardManager.GetInfo(mTask.UniqueNumber);
                if (uploadInfo != null)
                {
                    uploadInfo.UploadPercentage = (int)progress.Percentage;
                    mTask.MyWorker.ReportProgress((int)WorkerTask.ProgressType.CHANGE_TRAY_ICON_PROGRESS, progress);
                }
            }
        }

        public bool UploadDekiWiki()
        {
            try
            {
                string fullFilePath = mTask.LocalFilePath;

                if (Adapter.CheckDekiWikiAccounts(ref mTask) && File.Exists(fullFilePath))
                {
                    DekiWikiAccount acc = Engine.conf.DekiWikiAccountList[Engine.conf.DekiWikiSelected];

                    System.Net.IWebProxy proxy = Adapter.CheckProxySettings().GetWebProxy;

                    if (DekiWiki.savePath == null || DekiWiki.savePath.Length == 0 || Engine.conf.DekiWikiForcePath == true)
                    {
                        DekiWikiPath diag = new DekiWikiPath(new DekiWikiOptions(acc, proxy));
                        diag.history = acc.History;
                        diag.ShowDialog();

                        if (diag.DialogResult != DialogResult.OK)
                        {
                            throw new Exception("User canceled the operation.");
                        }

                        DekiWiki.savePath = diag.path;
                    }

                    mTask.DestinationName = acc.Name;

                    FileSystem.AppendDebug(string.Format("Uploading {0} to Mindtouch: {1}", mTask.FileName, acc.Url));

                    DekiWikiUploader uploader = new DekiWikiUploader(new DekiWikiOptions(acc, proxy));
                    // mTask.RemoteFilePath = acc.getUriPath(Path.GetFileName(mTask.LocalFilePath)); todo: check same output as getUriPath is possible else where
                    mTask.UpdateRemoteFilePath(uploader.UploadImage(mTask.LocalFilePath));

                    DekiWiki connector = new DekiWiki(new DekiWikiOptions(acc, proxy));
                    connector.UpdateHistory();

                    return true;
                }
            }
            catch (Exception ex)
            {
                mTask.Errors.Add("Mindtouch upload failed.\r\n" + ex.Message);
            }

            return false;
        }

        public bool UploadMediaWiki()
        {
            string fullFilePath = mTask.LocalFilePath;

            if (Adapter.CheckMediaWikiAccounts(ref mTask) && File.Exists(fullFilePath))
            {
                MediaWikiAccount acc = Engine.conf.MediaWikiAccountList[Engine.conf.MediaWikiAccountSelected];
                System.Net.IWebProxy proxy = Adapter.CheckProxySettings().GetWebProxy;
                mTask.DestinationName = acc.Name;
                FileSystem.AppendDebug(string.Format("Uploading {0} to MediaWiki: {1}", mTask.FileName, acc.Url));
                MediaWikiUploader uploader = new MediaWikiUploader(new MediaWikiOptions(acc, proxy));
                mTask.UpdateRemoteFilePath(uploader.UploadImage(mTask.LocalFilePath));
                // mTask.RemoteFilePath = acc.Url + "/index.php?title=File:" + (Path.GetFileName(mTask.LocalFilePath));

                return true;
            }
            return false;
        }

        public void UploadText()
        {
            mTask.StartTime = DateTime.Now;
            mTask.MyWorker.ReportProgress((int)WorkerTask.ProgressType.UPDATE_PROGRESS_MAX, TaskbarProgressBarState.Indeterminate);

            if (mTask.ShouldShortenURL(mTask.MyText))
            {
                // Need this for shortening URL using Clipboard Upload http://imgur.com/DzBJQ.png
                mTask.ShortenURL(mTask.MyText);
            }
            else
            {
                if (mTask.MyTextUploader == TextUploaderType.FileUploader)
                {
                    UploadFile();
                }
                else
                {
                    TextUploader textUploader = null;

                    switch (mTask.MyTextUploader)
                    {
                        case TextUploaderType.PASTE2:
                            textUploader = new Paste2Uploader();
                            break;
                        case TextUploaderType.PASTEBIN:
                            textUploader = new PastebinUploader(Engine.PastebinKey);
                            break;
                        case TextUploaderType.PASTEBIN_CA:
                            textUploader = new PastebinCaUploader(Engine.PastebinCaKey);
                            break;
                        case TextUploaderType.SLEXY:
                            textUploader = new SlexyUploader();
                            break;
                    }

                    if (textUploader != null)
                    {
                        FileSystem.AppendDebug("Uploading to " + textUploader.Name);

                        string url = string.Empty;

                        if (!string.IsNullOrEmpty(mTask.MyText))
                        {
                            url = textUploader.UploadText(mTask.MyText);
                        }
                        else
                        {
                            url = textUploader.UploadTextFile(mTask.LocalFilePath);
                        }

                        mTask.UpdateRemoteFilePath(new UploadResult() { URL = url });
                        mTask.Errors = textUploader.Errors;
                    }
                }
            }
            mTask.EndTime = DateTime.Now;
        }

        public void TextEdit()
        {
            if (File.Exists(mTask.LocalFilePath))
            {
                Process p = new Process();
                ProcessStartInfo psi = new ProcessStartInfo(Engine.conf.TextEditorActive.Path)
                {
                    Arguments = string.Format("{0}{1}{0}", "\"", mTask.LocalFilePath)
                };
                p.StartInfo = psi;
                p.Start();
                // Wait till user quits the ScreenshotEditApp
                p.WaitForExit();
            }
        }

        /// <summary>
        /// Perform Actions after capturing image/text/file objects
        /// </summary>
        public void PerformActions(WorkerTask task)
        {
            if (File.Exists(task.LocalFilePath))
            {
                foreach (Software app in Engine.conf.ActionsList)
                {
                    if (app.Enabled)
                    {
                        if (app.Name == Engine.ZSCREEN_IMAGE_EDITOR)
                        {
                            try
                            {
                                Greenshot.Configuration.AppConfig.ConfigPath = Path.Combine(Engine.SettingsDir, "ImageEditor.bin");
                                Greenshot.ImageEditorForm editor = new Greenshot.ImageEditorForm { Icon = Resources.zss_main };
                                editor.AutoSave = Engine.conf.ImageEditorAutoSave;
                                editor.MyWorker = task.MyWorker;
                                editor.SetImage(task.MyImage);
                                editor.SetImagePath(task.LocalFilePath);
                                editor.ShowDialog();
                            }
                            catch (Exception ex)
                            {
                                FileSystem.AppendDebug("ImageEdit", ex);
                            }
                        }
                        else if (File.Exists(app.Path))
                        {
                            if (task.Job1 == JobLevel1.File && app.TriggerForFiles ||
                                task.Job1 == JobLevel1.Image && app.TriggerForImages ||
                                task.Job1 == JobLevel1.Text && app.TriggerForText)
                            {
                                app.OpenFile(task.LocalFilePath);
                            }
                        }
                    }
                }
            }
        }

        public override string ToString()
        {
            StringBuilder sbDebug = new StringBuilder();
            sbDebug.AppendLine(string.Format("Image Uploader: {0", mTask.MyImageUploader));
            return sbDebug.ToString();
        }
    }
}