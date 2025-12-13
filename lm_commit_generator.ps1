Clear-Host
Write-Host "-----------------------------------------------"
Write-Host "create_commit_with_ai_v2_lmstudio.ps1 (LM Studio Destekli)"
Write-Host "LM Studio kullanarak otomatik git commit mesajı oluşturma"
Write-Host "-----------------------------------------------`n"

# --- CONFIGURATION ---
# LM Studio varsayılan API uç noktası
$lmStudioApiUrl = "http://localhost:1234/v1/chat/completions"
# LM Studio'da yüklediğiniz modelin tam adı (Örn: "QuantFactory/Meta-Llama-3-8B-Instruct-GGUF/Meta-Llama-3-8B-Instruct-Q4_K_M.gguf")
# LM Studio'da çalışan modelin adını kontrol edin!
$model = "MODEL_ADINIZI_BURAYA_GIRIN" 
# ----------------------

# Kontrol: LM Studio sunucusunun çalıştığından emin olun!

# AI'ya istek göndermek için fonksiyon
function Invoke-LMStudioAI {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prompt,
        
        [Parameter(Mandatory = $true)]
        [string]$Model
    )

    # Chat formatına uygun payload oluşturma
    $body = @{
        "model"       = $Model;
        "messages"    = @(
            @{
                "role"    = "system";
                "content" = "You are a concise AI code analyst. Your only task is to generate a brief, single-line description of the code changes provided."
            },
            @{
                "role"    = "user";
                "content" = $Prompt
            }
        );
        # Yüksek hızlı ve düşük maliyetli cevap için
        "temperature" = 0.3;
        "max_tokens"  = 100;
        "stream"      = $false
    } | ConvertTo-Json

    try {
        # LM Studio'ya API isteği gönderme
        $response = Invoke-RestMethod -Uri $lmStudioApiUrl -Method Post -Body $body -ContentType "application/json" -TimeoutSec 60

        # Cevaptan mesaj içeriğini çıkarma
        if ($response.choices -and $response.choices.count -gt 0) {
            $message = $response.choices[0].message.content.Trim()
            return $message.Trim('"').Trim("'")
        }
        else {
            return $null
        }
    }
    catch {
        Write-Host "❌ API isteği hatası: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

# Check if git is installed
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "❌ Git is not installed or not in PATH."
    exit
}

# ... (Dosya hazırlığı ve staging kısmı aynı kalıyor) ...
# Get unstaged and staged files
$unstaged = git diff --name-only
# Filter out empty lines AND exclude files ending with .g.dart or .freezed.dart
$staged_files = git diff --cached --name-only | 
Where-Object { 
    $_ -ne "" -and 
    $_ -notlike "*.g.dart" -and 
    $_ -notlike "*.freezed.dart" 
}

# If nothing staged, ask user if they want to stage all
if (-not $staged_files) {
    if ($unstaged) {
        Write-Host "⚠️  No staged changes found, but there are unstaged files:`n"
        Write-Host $unstaged -ForegroundColor Yellow
        $stageAnswer = Read-Host "`nDo you want to stage ALL changes? (y/n)"
        if ($stageAnswer -eq "y") {
            git add -A
            Write-Host "`n✅ All changes staged."
            # Re-fetch staged files, applying the same filter
            $staged_files = git diff --cached --name-only | 
            Where-Object { 
                $_ -ne "" -and 
                $_ -notlike "*.g.dart" -and 
                $_ -notlike "*.freezed.dart" 
            }

            if (-not $staged_files) {
                Write-Host "`n⚠️ All files were staged, but only auto-generated files remained for processing. No files to analyze. Exiting." -ForegroundColor DarkYellow
                exit
            }
        }
        else {
            Write-Host "`n❌ No files staged. Exiting."
            exit
        }
    }
    else {
        Write-Host "⚠️  No modified or staged files found. Nothing to commit."
        exit
    }
}
# ... (Dosya hazırlığı ve staging kısmı aynı kalıyor) ...

Write-Host "`n🔍 Analyzing staged files with $model (LM Studio)...`n"

# Analyze staged changes file-by-file
$combined_commit_messages = @()
$display_output = "" # Holds the output to be displayed on screen

foreach ($file in $staged_files) {
    Write-Host "Processing file: **$file**" -ForegroundColor Yellow

    # Get diff content only for the current file
    $file_diff = git diff --cached -- "$file"

    if (-not $file_diff) {
        Write-Host "   ⚠️ Could not get staged diff for **$file**. Skipping." -ForegroundColor DarkYellow
        continue
    }

    # Prepare AI prompt
    # System mesajı fonksiyonda tanımlandığı için, burada sadece diff içeriği gönderilir.
    $prompt = "Generate a concise, single-line description of the changes in this file diff. Do not include the file name in the output. Diff for '$file':`n$file_diff"

    # Generate commit message using LM Studio API
    $ai_response = Invoke-LMStudioAI -Prompt $prompt -Model $model

    if (-not $ai_response) {
        $message = "❌ Failed to get response for **$file**. (API or Model Error)"
        Write-Host $message -ForegroundColor Red
        # Fix: Delimit variable with curly braces
        $combined_commit_messages += "* ${file}: Failed to generate AI message."
        $display_output += "* **$file**: $message`n"
    }
    else {
        $message = $ai_response
        
        Write-Host "   ✅ Result: $message" -ForegroundColor Green
        
        # Fix: Delimit variable with curly braces
        # Format for the final combined message (as a bulleted list)
        $combined_commit_messages += "* ${file}: $message"
        
        # Format for screen display
        $display_output += "* **$file**: $message`n"
    }
}

# Create the final main commit message
$final_commit_message_list = $combined_commit_messages -join "`n"

# Optionally, send the list back to LM Studio to generate a single summary message
Write-Host "`n🧠 Generating final summary commit message..."
$summary_prompt = "Generate a concise, high-level git commit message summarizing the following list of changes (Do not include the list itself in the final message):`n$final_commit_message_list"

# Use the same API function for the summary
$summary_message = Invoke-LMStudioAI -Prompt $summary_prompt -Model $model

# --- DISPLAY RESULTS ---
Write-Host "`n============================================"
Write-Host "  ✅ AI-GENERATED COMMIT ANALYSIS" -ForegroundColor Cyan
Write-Host "============================================"

Write-Host "`n## Per-File Analysis Results (Data Sent and Result Received)`n"
Write-Host $display_output

Write-Host "`n---"
Write-Host "## 📝 Suggested Main Commit Message (Summary)" -ForegroundColor Cyan
Write-Host $summary_message -ForegroundColor Green
Write-Host "---`n"

if (-not $summary_message) {
    Write-Host "`n❌ Final summary message generation failed. Exiting."
    exit
}

# Confirm commit
$answer = Read-Host "Do you want to commit with this summary message? (y/n)"
if ($answer -eq "y") {
    # Trim and remove quotes
    $commit_message = $summary_message.Trim('"')

    # Escape any remaining double quotes just in case
    $commit_message = $commit_message -replace '"', '\"'

    # Commit
    git commit -m "$commit_message"

    Write-Host "`n✅ Commit created successfully."

    # Ask to push
    $pushAnswer = Read-Host "`nDo you want to push the commit to remote? (y/n)"
    if ($pushAnswer -eq "y") {
        Write-Host "`n🚀 Pushing to remote..."
        git push
        if ($LASTEXITCODE -eq 0) {
            Write-Host "`n✅ Push completed successfully."
        }
        else {
            Write-Host "`n⚠️ Push failed. Please check your connection or credentials."
        }
    }
    else {
        Write-Host "`nPush skipped."
    }
}
else {
    Write-Host "`nCommit cancelled."
}