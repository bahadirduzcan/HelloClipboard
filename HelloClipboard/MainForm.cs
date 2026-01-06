using HelloClipboard.Html;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Drawing.Imaging;

namespace HelloClipboard
{
	public partial class MainForm : Form
	{
		private readonly TrayApplicationContext _trayApplicationContext;
		private readonly MainFormViewModel _viewModel;
		private bool _isLoaded = false;
		private Form _openDetailForm;
		private ClipDetailText _detailTextForm;
		private ClipDetailImage _detailImageForm;
		private FormWindowState _previousWindowState = FormWindowState.Normal;
		private bool _useRegexSearch = false;
		private bool _caseSensitiveSearch = false;
		private string _currentSearchTerm = string.Empty;
		private Regex _currentRegex = null;

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

		public MainForm(TrayApplicationContext trayApplicationContext)
		{
			InitializeComponent();

			this.Text = Application.ProductName + " v" + Application.ProductVersion;

			_trayApplicationContext = trayApplicationContext;
			_viewModel = new MainFormViewModel(this, trayApplicationContext);

			_viewModel.LoadSettings();

			MessagesListBox.DisplayMember = "Title";
			MessagesListBox.DrawMode = DrawMode.OwnerDrawFixed;
			MessagesListBox.DrawItem += MessagesListBox_DrawItem;
			MessagesListBox.Resize += (s, e) => MessagesListBox.Invalidate(); // force redraw so ellipsis reflows after resize

			var enableClipboardHistory = SettingsLoader.Current.EnableClipboardHistory;

			this.Deactivate += MainForm_Deactivate;

			MessagesListBox.SelectedIndexChanged += MessagesListBox_SelectedIndexChanged;
			textBox1_search.KeyDown += textBox1_search_KeyDown;
			textBox1_search.KeyPress += textBox1_search_KeyPress;
		}

		private void textBox1_search_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Control && e.KeyCode == Keys.Back)
			{
				DeletePreviousWord(textBox1_search);
				e.SuppressKeyPress = true;
				return;
			}
			if (e.Control && e.KeyCode == Keys.Delete)
			{
				DeleteNextWord(textBox1_search);
				e.SuppressKeyPress = true;
				return;
			}
			if (MessagesListBox.Items.Count == 0) return;

			int currentIndex = MessagesListBox.SelectedIndex;

			if (e.KeyCode == Keys.Down)
			{
				// Bir alt satıra geç (liste sonundaysa başa dönme veya durma tercihi sana ait)
				if (currentIndex < MessagesListBox.Items.Count - 1)
					MessagesListBox.SelectedIndex = currentIndex + 1;

				e.Handled = true; // Windows'un bip sesini ve imleç hareketini engeller
				e.SuppressKeyPress = true;
			}
			else if (e.KeyCode == Keys.Up)
			{
				// Bir üst satıra geç
				if (currentIndex > 0)
					MessagesListBox.SelectedIndex = currentIndex - 1;
				else if (currentIndex == -1 && MessagesListBox.Items.Count > 0)
					MessagesListBox.SelectedIndex = 0;

				e.Handled = true;
				e.SuppressKeyPress = true;
			}
			else if (e.KeyCode == Keys.Enter)
			{
				// Enter'a basıldığında seçili öğeyi kopyala ve kapat
				if (MessagesListBox.SelectedItem is ClipboardItem selectedItem)
				{
					_viewModel.CopyClicked(selectedItem);
					_trayApplicationContext.HideMainWindow();
				}
				e.Handled = true;
				e.SuppressKeyPress = true;
			}
		}

		private void MessagesListBox_SelectedIndexChanged(object sender, EventArgs e)
		{
			if (MessagesListBox.SelectedIndex >= 0)
			{
				// Klavye ile gezerken detay penceresini otomatik aç/güncelle
				OpenDetailForIndex(MessagesListBox.SelectedIndex);

				// Odağı tekrar arama kutusuna çek (Detay formu odağı çalmasın diye)
				textBox1_search.Focus();

				if (MessagesListBox.SelectedItem is ClipboardItem selected)
				{
					pinToolStripMenuItem.Checked = selected.IsPinned;
					pinToolStripMenuItem.Text = selected.IsPinned ? "Unpin" : "Pin";
				}
			}
		}

		private void DeletePreviousWord(TextBox tb)
		{
			int pos = tb.SelectionStart;
			if (pos == 0) return;

			string text = tb.Text;
			int start = pos;

			while (start > 0 && char.IsWhiteSpace(text[start - 1]))
				start--;
			while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
				start--;

			int length = pos - start;
			if (length <= 0) return;

			tb.Text = text.Remove(start, length);
			tb.SelectionStart = start;
		}

		private void DeleteNextWord(TextBox tb)
		{
			int pos = tb.SelectionStart;
			string text = tb.Text;
			if (pos >= text.Length) return;

			int end = pos;
			while (end < text.Length && char.IsWhiteSpace(text[end]))
				end++;
			while (end < text.Length && !char.IsWhiteSpace(text[end]))
				end++;

			int length = end - pos;
			if (length <= 0) return;

			tb.Text = text.Remove(pos, length);
			tb.SelectionStart = pos;
		}

		private bool IsAppFocused()
		{
			IntPtr foregroundWindow = GetForegroundWindow();
			if (foregroundWindow == IntPtr.Zero) return false;

			uint foregroundProcessId;
			GetWindowThreadProcessId(foregroundWindow, out foregroundProcessId);

			// Şu anki çalışan sürecin (Process) ID'si ile öndeki pencerenin ID'sini karşılaştır
			return foregroundProcessId == (uint)Process.GetCurrentProcess().Id;
		}

		private void MainForm_Deactivate(object sender, EventArgs e)
		{
			if (!SettingsLoader.Current.AutoHideWhenUnfocus || !_isLoaded)
				return;

			// Task.Delay yerine kontrolü bir tık sonraya atmak için
			BeginInvoke(new MethodInvoker(async () =>
			{
				// Windows'un odağı tam devretmesi için çok kısa bir bekleme
				await System.Threading.Tasks.Task.Delay(100);

				if (!IsAppFocused())
				{
					// Eğer öndeki pencere bizim uygulamamıza ait değilse gizle
					_trayApplicationContext.HideMainWindow();
				}
			}));
		}

		private bool IsFormOwnedByMe(Form activeForm)
		{
			if (activeForm == null) return false;

			return activeForm == this ||
				   activeForm.Owner == this ||
				   _openDetailForm != null && activeForm == _openDetailForm;
		}

		public void MessageAdd(ClipboardItem item)
		{
			if (SettingsLoader.Current.InvertClipboardHistoryListing)
			{
				if (item.IsPinned)
				{
					MessagesListBox.Items.Insert(0, item);
					MessagesListBox.TopIndex = 0;
				}
				else
				{
					int offset = MessagesListBox.Items.Cast<object>().TakeWhile(i => (i as ClipboardItem)?.IsPinned == true).Count();
					MessagesListBox.Items.Insert(offset, item);
					MessagesListBox.TopIndex = offset;
				}
			}
			else
			{
				if (item.IsPinned)
				{
					MessagesListBox.Items.Insert(0, item);
				}
				else
				{
					int offset = MessagesListBox.Items.Cast<object>().TakeWhile(i => (i as ClipboardItem)?.IsPinned == true).Count();
					MessagesListBox.Items.Insert(offset, item);
				}
				int lastIndex = MessagesListBox.Items.Count - 1;
				if (lastIndex >= 0)
				{
					MessagesListBox.TopIndex = lastIndex;
				}
			}
		}

		public void RemoveOldestMessage()
		{
			if (MessagesListBox.Items.Count == 0)
				return;

			int removeIndex;

			if (SettingsLoader.Current.InvertClipboardHistoryListing)
			{
				// Pinned kalemler liste başında, invert modda sondan sil
				int lastIndex = MessagesListBox.Items.Count - 1;
				while (lastIndex >= 0)
				{
					if ((MessagesListBox.Items[lastIndex] as ClipboardItem)?.IsPinned == true)
					{
						lastIndex--;
						continue;
					}
					break;
				}
				if (lastIndex < 0)
					return;
				removeIndex = lastIndex;
			}
			else
			{
				// Normal listede ilk unpinned öğeyi sil
				int idx = 0;
				while (idx < MessagesListBox.Items.Count)
				{
					if ((MessagesListBox.Items[idx] as ClipboardItem)?.IsPinned == true)
					{
						idx++;
						continue;
					}
					break;
				}
				if (idx >= MessagesListBox.Items.Count)
					return;
				removeIndex = idx;
			}

			MessagesListBox.Items.RemoveAt(removeIndex);

			int lastIndexAfterRemoval = MessagesListBox.Items.Count - 1;
			if (lastIndexAfterRemoval >= 0)
			{
				MessagesListBox.TopIndex = lastIndexAfterRemoval;
			}
		}

		public void MessageRemoveItem(ClipboardItem item)
		{
			if (MessagesListBox.Items.Contains(item))
			{
				MessagesListBox.Items.Remove(item);
				MessagesListBox.Refresh();
			}
		}

		private void MainForm_Shown(object sender, EventArgs e)
		{
			RefreshCacheView();
		}

		public void RefreshCacheView()
		{
			MessagesListBox.BeginUpdate();
			MessagesListBox.Items.Clear();
			var cache = _trayApplicationContext.GetClipboardCache();
			foreach (var item in cache)
			{
				// MessageAdd, item'ın tarih sırasına göre (eskiden yeniye) 
				// gelmesini bekler ve ayar aktifse yeni geleni hep 0. indexe koyar.
				MessageAdd(item);
			}
			MessagesListBox.EndUpdate();
		}

		public void UpdateCheckUpdateNowBtnText(string newString)
		{
			checkUpdateToolStripMenuItem.Text = newString;
		}

		private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
		{
			using (var dlg = new InfoDialog(AboutHtml.GetTitle(), AboutHtml.GetHtml()))
			{
				dlg.ShowDialog(this);
			}
		}

		private void helpToolStripMenuItem_Click(object sender, EventArgs e)
		{
			using (var dlg = new InfoDialog(HelpHtml.GetTitle(), HelpHtml.GetHtml()))
			{
				dlg.ShowDialog(this);
			}
		}

		private void ShowUnderDevelopmentDialog(string featureName)
		{
			using (var dlg = new InfoDialog(UnderDevelopmentHtml.GetTitle(), UnderDevelopmentHtml.GetHtml(featureName)))
			{
				dlg.ShowDialog(this);
			}
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (_trayApplicationContext.ApplicationExiting)
			{
				base.OnFormClosing(e);
				return;
			}
			if (!SettingsLoader.Current.HideToTray || TempConfigLoader.Current.AdminPriviligesRequested)
			{
				_trayApplicationContext.ExitApplication();
			}
			else
			{
				e.Cancel = true;
				_trayApplicationContext.HideMainWindow();
			}
		}

		public void copyToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (MessagesListBox.SelectedIndices.Count == 0)
				return;
			CloseDetailFormIfAvaible();
			ClipboardItem selectedItem = MessagesListBox.SelectedItem as ClipboardItem;
			_viewModel.CopyClicked(selectedItem);
		}

		private void OpenDetailForIndex(int index)
		{
			if (index < 0) return;

			MessagesListBox.SelectedIndex = index;

			if (!(MessagesListBox.SelectedItem is ClipboardItem selectedItem))
				return;

			Rectangle previousBounds = _openDetailForm != null && !_openDetailForm.IsDisposed ? _openDetailForm.Bounds : Rectangle.Empty;

			if (selectedItem.ItemType == ClipboardItemType.Image)
			{
				if (_detailImageForm == null || _detailImageForm.IsDisposed)
				{
					_detailImageForm = new ClipDetailImage(this, selectedItem);
					_detailImageForm.Deactivate += MainForm_Deactivate;
					_detailImageForm.Owner = this;
				}
				else
				{
					_detailImageForm.UpdateItem(selectedItem);
				}

				EnsureDetailPosition(_detailImageForm, previousBounds);
				ShowDetailForm(_detailImageForm);

				if (_detailTextForm != null && !_detailTextForm.IsDisposed)
					_detailTextForm.Hide();

				_openDetailForm = _detailImageForm;
			}
			else
			{
				if (_detailTextForm == null || _detailTextForm.IsDisposed)
				{
					_detailTextForm = new ClipDetailText(this, selectedItem);
					_detailTextForm.Deactivate += MainForm_Deactivate;
					_detailTextForm.Owner = this;
				}
				else
				{
					_detailTextForm.UpdateItem(selectedItem);
				}

				EnsureDetailPosition(_detailTextForm, previousBounds);
				ShowDetailForm(_detailTextForm);

				if (_detailImageForm != null && !_detailImageForm.IsDisposed)
					_detailImageForm.Hide();

				_openDetailForm = _detailTextForm;
			}
		}

		private void contextMenuStrip1_Opening(object sender, CancelEventArgs e)
		{
			var pos = MessagesListBox.PointToClient(Cursor.Position);
			int index = MessagesListBox.IndexFromPoint(pos);
			if (index < 0)
			{
				e.Cancel = true;
				return;
			}
			OpenDetailForIndex(index);
			if (MessagesListBox.SelectedItem is ClipboardItem selected)
			{
				pinToolStripMenuItem.Checked = selected.IsPinned;
				pinToolStripMenuItem.Text = selected.IsPinned ? "Unpin" : "Pin";

				var isUrl = selected.ItemType == ClipboardItemType.Text && IsValidUrl(selected.Content);
				openUrlToolStripMenuItem.Visible = isUrl;

				var fileExists = selected.ItemType != ClipboardItemType.File || (!string.IsNullOrWhiteSpace(selected.Content) && File.Exists(selected.Content));
				saveToFileToolStripMenuItem.Enabled = fileExists;
			}
		}
		private void MessagesListBox_MouseClick(object sender, MouseEventArgs e)
		{
			if (e.Button != MouseButtons.Left) return;

			int index = MessagesListBox.IndexFromPoint(e.Location);
			OpenDetailForIndex(index);
		}

		private void MainForm_Load(object sender, EventArgs e)
		{
			var cfg = TempConfigLoader.Current;
			if (cfg.MainFormWidth > 0 && cfg.MainFormHeight > 0)
				this.Size = new Size(cfg.MainFormWidth, cfg.MainFormHeight);
			if (cfg.MainFormX >= 0 && cfg.MainFormY >= 0)
			{
				this.StartPosition = FormStartPosition.Manual;
				this.Location = new Point(cfg.MainFormX, cfg.MainFormY);
			}

			if (SettingsLoader.Current.AlwaysTopMost)
			{
				this.TopMost = true;
			}
			CheckAndUpdateTopMostImage();

			this.ShowInTaskbar = SettingsLoader.Current.ShowInTaskbar;

			_isLoaded = true;
		}

		public void UpdateTaskbarVisibility(bool visible)
		{
			this.ShowInTaskbar = visible;
		}

		private void _SaveFormPosition()
		{
			if (this.WindowState == FormWindowState.Normal && _isLoaded)
			{
				var cfg = TempConfigLoader.Current;
				cfg.MainFormWidth = this.Width;
				cfg.MainFormHeight = this.Height;
				cfg.MainFormX = this.Location.X;
				cfg.MainFormY = this.Location.Y;
				TempConfigLoader.Save();
			}
		}

		public void CloseDetailFormIfAvaible()
		{
			if (_detailTextForm != null && !_detailTextForm.IsDisposed)
				_detailTextForm.Hide();
			if (_detailImageForm != null && !_detailImageForm.IsDisposed)
				_detailImageForm.Hide();
			_openDetailForm = null;
		}
		private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
		{
			CloseDetailFormIfAvaible();
			_SaveFormPosition();
		}

		private async void MainForm_Resize(object sender, EventArgs e)
		{
			_SaveFormPosition();
			if (_isLoaded && this.WindowState == FormWindowState.Normal &&
		_previousWindowState == FormWindowState.Maximized)
			{
				await System.Threading.Tasks.Task.Delay(10);
				var cfg = TempConfigLoader.Current;
				if (cfg.MainFormX >= 0 && cfg.MainFormY >= 0)
				{
					this.Location = new Point(cfg.MainFormX, cfg.MainFormY);
				}
			}
			_previousWindowState = this.WindowState;

			if (_openDetailForm != null && !_openDetailForm.IsDisposed && _openDetailForm.Visible)
			{
				EnsureDetailPosition(_openDetailForm, Rectangle.Empty);
				ShowDetailForm(_openDetailForm);
			}
		}

		private void MainForm_Move(object sender, EventArgs e)
		{
			var screen = Screen.FromControl(this).WorkingArea;
			int snapDistance = 20;
			int newX = this.Left;
			int newY = this.Top;
			bool snappedX = false;
			bool snappedY = false;
			if (Math.Abs(this.Right - screen.Right) < snapDistance)
			{
				newX = screen.Right - this.Width;
				snappedX = true;
			}
			else if (Math.Abs(this.Left - screen.Left) < snapDistance)
			{
				newX = screen.Left;
				snappedX = true;
			}
			if (Math.Abs(this.Bottom - screen.Bottom) < snapDistance)
			{
				newY = screen.Bottom - this.Height;
				snappedY = true;
			}
			else if (Math.Abs(this.Top - screen.Top) < snapDistance)
			{
				newY = screen.Top;
				snappedY = true;
			}
			if (snappedX || snappedY)
			{
				this.Location = new Point(newX, newY);
			}
			if (_openDetailForm != null && !_openDetailForm.IsDisposed)
			{
				PositionDetailForm(_openDetailForm);
			}
		}

		public void ResetFormPositionAndSize()
		{
			Size = new Size(480, 720);
			StartPosition = FormStartPosition.CenterScreen;
			WindowState = FormWindowState.Normal;
		}

		private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
		{
			using (var f = new SettingsForm(this))
			{
				f.StartPosition = FormStartPosition.CenterParent;
				f.ShowDialog(this);
			}
		}

		private void PositionDetailForm(Form detailForm)
		{
			var mainRect = this.Bounds;
			var screen = Screen.FromControl(this).WorkingArea;
			int padding = 1;
			Point location = new Point();
			if (mainRect.Right + detailForm.Width + padding <= screen.Right)
			{
				location = new Point(mainRect.Right + padding, mainRect.Top);
			}
			else if (mainRect.Left - detailForm.Width - padding >= screen.Left)
			{
				location = new Point(mainRect.Left - detailForm.Width - padding, mainRect.Top);
			}
			else if (mainRect.Bottom + detailForm.Height + padding <= screen.Bottom)
			{
				location = new Point(mainRect.Left, mainRect.Bottom + padding);
			}
			else if (mainRect.Top - detailForm.Height - padding >= screen.Top)
			{
				location = new Point(mainRect.Left, mainRect.Top - detailForm.Height - padding);
			}
			else
			{
				location = new Point(
					Math.Max(screen.Left, screen.Left + (screen.Width - detailForm.Width) / 2),
					Math.Max(screen.Top, screen.Top + (screen.Height - detailForm.Height) / 2)
				);
			}
			detailForm.StartPosition = FormStartPosition.Manual;
			detailForm.Location = location;
		}

		private void EnsureDetailPosition(Form detailForm, Rectangle previousBounds)
		{
			if (!previousBounds.IsEmpty)
			{
				detailForm.StartPosition = FormStartPosition.Manual;
				detailForm.Bounds = previousBounds;
			}
			else
			{
				PositionDetailForm(detailForm);
			}
		}

		private void ShowDetailForm(Form detailForm)
		{
			if (detailForm == null || detailForm.IsDisposed)
				return;

			detailForm.TopMost = this.TopMost;
			if (!detailForm.Visible)
			{
				detailForm.Show();
			}
			detailForm.BringToFront();
			detailForm.Activate();
		}

		private void phoneSyncToolStripMenuItem_Click(object sender, EventArgs e)
		{
			ShowUnderDevelopmentDialog("Phone Sync");
		}

		public void FocusSearchBox()
		{
			textBox1_search.Focus();
		}

		public void ClearSearchBox()
		{
			textBox1_search.Text = string.Empty;
		}

		private void textBox1_search_TextChanged(object sender, EventArgs e)
		{
			_currentSearchTerm = textBox1_search.Text;
			CloseDetailFormIfAvaible();
			MessagesListBox.ClearSelected();
			BuildSearchRegex();
			FilterClipboardCache(_currentSearchTerm);
		}

		private void textBox1_search_KeyPress(object sender, KeyPressEventArgs e)
		{
			// Ctrl ile basılan kontrol karakterlerinin kutu olarak görünmesini engelle
			if (char.IsControl(e.KeyChar) && e.KeyChar != '\b')
			{
				e.Handled = true;
			}
		}

		private void FilterClipboardCache(string searchTerm)
		{
			MessagesListBox.BeginUpdate();
			MessagesListBox.Items.Clear();

			var cache = _trayApplicationContext.GetClipboardCache();
			IEnumerable<ClipboardItem> itemsToDisplay;

			if (string.IsNullOrWhiteSpace(searchTerm))
			{
				itemsToDisplay = cache;
			}
			else if (_useRegexSearch && _currentRegex != null)
			{
				itemsToDisplay = cache.Where(i =>
				{
					if (i.Content == null) return false;
					return _currentRegex.IsMatch(i.Content);
				});
			}
			else
			{
				if (_caseSensitiveSearch)
					itemsToDisplay = cache.Where(i => i.Content != null && i.Content.Contains(searchTerm));
				else
					itemsToDisplay = cache.Where(i => i.Content != null && i.Content.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0);
			}

			foreach (var item in itemsToDisplay)
			{
				// Direkt Add(item) yaparsanız ayarı görmezden gelip sona ekler.
				// MessageAdd(item) metodunu kullanırsanız ayara göre başa veya sona ekler.
				MessageAdd(item);
			}

			MessagesListBox.EndUpdate();
		}

		private void BuildSearchRegex()
		{
			_currentRegex = null;
			if (!_useRegexSearch || string.IsNullOrWhiteSpace(_currentSearchTerm))
				return;
			try
			{
				var options = _caseSensitiveSearch ? RegexOptions.None : RegexOptions.IgnoreCase;
				_currentRegex = new Regex(_currentSearchTerm, options);
			}
			catch
			{
				_currentRegex = null;
			}
		}

		private void pictureBox2_Click(object sender, EventArgs e)
		{
			var result = MessageBox.Show(
				"Are you sure you want to clear the clipboard history?",
				"Clear Clipboard",
				MessageBoxButtons.YesNo);
			if (result == DialogResult.Yes)
				_trayApplicationContext.ClearClipboard();
		}

		private void checkBoxRegex_CheckedChanged(object sender, EventArgs e)
		{
			_useRegexSearch = checkBoxRegex.Checked;
			BuildSearchRegex();
			FilterClipboardCache(_currentSearchTerm);
		}

		private void checkBoxCaseSensitive_CheckedChanged(object sender, EventArgs e)
		{
			_caseSensitiveSearch = checkBoxCaseSensitive.Checked;
			BuildSearchRegex();
			FilterClipboardCache(_currentSearchTerm);
		}

		private void pinToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (MessagesListBox.SelectedItem is ClipboardItem selected)
			{
				selected.IsPinned = !selected.IsPinned;
				if (selected.ContentHash != null)
				{
					if (selected.IsPinned)
					{
						if (!TempConfigLoader.Current.PinnedHashes.Contains(selected.ContentHash))
							TempConfigLoader.Current.PinnedHashes.Add(selected.ContentHash);
					}
					else
					{
						TempConfigLoader.Current.PinnedHashes.Remove(selected.ContentHash);
					}
					TempConfigLoader.Save();
				}

				MessagesListBox.BeginUpdate();
				MessagesListBox.Items.Remove(selected);
				if (selected.IsPinned)
				{
					MessagesListBox.Items.Insert(0, selected);
				}
				else
				{
					int offset = MessagesListBox.Items.Cast<object>().TakeWhile(i => (i as ClipboardItem)?.IsPinned == true).Count();
					MessagesListBox.Items.Insert(offset, selected);
				}
				MessagesListBox.EndUpdate();
				MessagesListBox.Refresh();

				pinToolStripMenuItem.Checked = selected.IsPinned;
				pinToolStripMenuItem.Text = selected.IsPinned ? "Unpin" : "Pin";
			}
		}

		private void saveToFileToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (!(MessagesListBox.SelectedItem is ClipboardItem selected))
				return;

			try
			{
				if (selected.ItemType == ClipboardItemType.Image)
				{
					SaveImageItem(selected);
				}
				else if (selected.ItemType == ClipboardItemType.File)
				{
					SaveFileItem(selected);
				}
				else
				{
					SaveTextItem(selected);
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Kaydetme sırasında hata oluştu.\n{ex.Message}", "Kaydetme hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private void SaveImageItem(ClipboardItem item)
		{
			if (item.ImageContent == null)
			{
				MessageBox.Show("Kaydedilecek görsel bulunamadı.", "Görsel kaydetme", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			using (var dialog = new SaveFileDialog
			{
				Title = "Resmi Kaydet",
				Filter = "PNG Image|*.png|JPEG Image|*.jpg;*.jpeg|Bitmap Image|*.bmp|All files|*.*",
				FileName = "clipboard"
			})
			{
				if (dialog.ShowDialog(this) == DialogResult.OK)
				{
					var ext = Path.GetExtension(dialog.FileName)?.ToLowerInvariant();
					var format = ImageFormat.Png;
					if (ext == ".jpg" || ext == ".jpeg")
						format = ImageFormat.Jpeg;
					else if (ext == ".bmp")
						format = ImageFormat.Bmp;

					item.ImageContent.Save(dialog.FileName, format);
				}
			}
		}

		private void SaveFileItem(ClipboardItem item)
		{
			var sourcePath = item.Content;
			if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
			{
				MessageBox.Show("Kaynak dosya bulunamadı.", "Dosya kaydetme", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			using (var dialog = new SaveFileDialog
			{
				Title = "Dosyayı Farklı Kaydet",
				FileName = Path.GetFileName(sourcePath),
				Filter = "All files|*.*",
				InitialDirectory = Path.GetDirectoryName(sourcePath)
			})
			{
				if (dialog.ShowDialog(this) == DialogResult.OK)
				{
					File.Copy(sourcePath, dialog.FileName, true);
				}
			}
		}

		private void SaveTextItem(ClipboardItem item)
		{
			var content = item.Content ?? string.Empty;

			using (var dialog = new SaveFileDialog
			{
				Title = "Metni Kaydet",
				Filter = "Text File|*.txt|All files|*.*",
				FileName = "clipboard.txt",
				DefaultExt = "txt",
				AddExtension = true
			})
			{
				if (dialog.ShowDialog(this) == DialogResult.OK)
				{
					File.WriteAllText(dialog.FileName, content, Encoding.UTF8);
				}
			}
		}

		private void openUrlToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (!(MessagesListBox.SelectedItem is ClipboardItem selected))
				return;

			var url = selected.Content?.Trim();
			if (!IsValidUrl(url))
			{
				MessageBox.Show("Geçerli bir URL bulunamadı.", "URL açma", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = url,
					UseShellExecute = true
				});
			}
			catch (Exception ex)
			{
				MessageBox.Show($"URL açılamadı.\n{ex.Message}", "URL açma hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private bool IsValidUrl(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return false;

			text = text.Trim();

			if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
			{
				return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
			}

			return false;
		}

		private async void checkUpdateToolStripMenuItem_Click(object sender, EventArgs e)
		{
			checkUpdateToolStripMenuItem.Enabled = false;
			string currentVersion = Application.ProductVersion;
			var update = await UpdateService.CheckForUpdateAsync(currentVersion, false);
			if (update != null)
			{
				var result = MessageBox.Show(
					"A new version is available. Do you want to download it now? New update features: " + update.Notes,
					"Update Available v" + update.Version,
					MessageBoxButtons.YesNo,
					MessageBoxIcon.Information);
				if (result == DialogResult.Yes)
				{
					await UpdateService.DownloadAndRunUpdateAsync();
				}
			}
			else
			{
				MessageBox.Show("No updates available.", "Up to date", MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
			checkUpdateToolStripMenuItem.Enabled = true;
		}

		public void CheckAndUpdateTopMostImage()
		{
			if (this.TopMost)
				pictureBox3_topMost.Image = Properties.Resources.icons8_locked_192px;
			else
				pictureBox3_topMost.Image = Properties.Resources.icons8_unlocked_192px;
		}

		private void pictureBox3_topMost_Click(object sender, EventArgs e)
		{
			if (!this.TopMost)
			{
				this.TopMost = true;
			}
			else
			{
				if (SettingsLoader.Current.AlwaysTopMost)
				{
					MessageBox.Show("The 'Always Top Most' setting is enabled in settings. Please disable it there to turn off top-most behavior.", "Action Not Allowed", MessageBoxButtons.OK, MessageBoxIcon.Information);
					return;
				}
				this.TopMost = false;
			}
			CheckAndUpdateTopMostImage();
		}

		private void MessagesListBox_DrawItem(object sender, DrawItemEventArgs e)
		{
			e.DrawBackground();
			if (e.Index < 0 || e.Index >= MessagesListBox.Items.Count)
				return;

			var item = MessagesListBox.Items[e.Index] as ClipboardItem;
			if (item == null)
				return;

			bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
			Color textColor = selected ? SystemColors.HighlightText : SystemColors.ControlText;
			var bounds = e.Bounds;

			string displayText = item.Title ?? string.Empty;
			if (item.IsPinned)
				displayText = "[PIN] " + displayText;

			DrawTextWithHighlight(e.Graphics, displayText, e.Font, textColor, bounds, selected);
			e.DrawFocusRectangle();
		}

		private void DrawTextWithHighlight(Graphics g, string text, Font font, Color textColor, Rectangle bounds, bool selected)
		{
			if (string.IsNullOrEmpty(text))
				return;

			var format = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

			if (string.IsNullOrWhiteSpace(_currentSearchTerm))
			{
				TextRenderer.DrawText(g, text, font, bounds, textColor, format);
				return;
			}

			List<(string part, bool highlight)> parts = new List<(string, bool)>();

			if (_useRegexSearch && _currentRegex != null)
			{
				int lastIndex = 0;
				foreach (Match m in _currentRegex.Matches(text))
				{
					if (m.Index > lastIndex)
					{
						parts.Add((text.Substring(lastIndex, m.Index - lastIndex), false));
					}
					parts.Add((text.Substring(m.Index, m.Length), true));
					lastIndex = m.Index + m.Length;
				}
				if (lastIndex < text.Length)
				{
					parts.Add((text.Substring(lastIndex), false));
				}
			}
			else
			{
				StringComparison comp = _caseSensitiveSearch ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
				int start = 0;
				while (true)
				{
					int idx = text.IndexOf(_currentSearchTerm, start, comp);
					if (idx < 0)
					{
						parts.Add((text.Substring(start), false));
						break;
					}
					if (idx > start)
					{
						parts.Add((text.Substring(start, idx - start), false));
					}
					parts.Add((text.Substring(idx, _currentSearchTerm.Length), true));
					start = idx + _currentSearchTerm.Length;
					if (start >= text.Length)
						break;
				}
			}

			int x = bounds.Left;
			foreach (var (part, highlight) in parts)
			{
				if (string.IsNullOrEmpty(part))
					continue;
				var size = TextRenderer.MeasureText(g, part, font, new Size(int.MaxValue, int.MaxValue), format);
				var rect = new Rectangle(x, bounds.Top, size.Width, bounds.Height);
				if (highlight)
				{
					Color back = selected ? Color.Gold : Color.Yellow;
					using (var brush = new SolidBrush(back))
					{
						g.FillRectangle(brush, rect);
					}
				}
				TextRenderer.DrawText(g, part, font, rect, textColor, format);
				x += size.Width;
				if (x > bounds.Right)
					break;
			}
		}
	}
}
