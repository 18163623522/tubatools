package com.tubawinui3.remote.ui

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.tubawinui3.remote.RemoteViewModel
import com.tubawinui3.remote.data.ApiClient
import com.tubawinui3.remote.data.ApiException
import com.tubawinui3.remote.data.ChatMessage
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File
import java.text.DateFormat
import java.util.Date

private const val MAX_PREVIEW_BYTES = 12L * 1024 * 1024

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatScreen(vm: RemoteViewModel, client: ApiClient, onBack: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var text by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()

    fun toast(s: String) = Toast.makeText(ctx, s, Toast.LENGTH_SHORT).show()

    fun upload(uri: Uri, isImage: Boolean) {
        val (name, size) = queryNameSize(ctx, uri)
        busy = true
        scope.launch {
            try {
                val m = client.chatUpload(name, isImage, size) {
                    ctx.contentResolver.openInputStream(uri) ?: throw ApiException("无法读取所选文件")
                }
                vm.addMessage(m)
            } catch (e: ApiException) {
                toast("发送失败：${e.message}")
            } finally {
                busy = false
            }
        }
    }

    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { it?.let { u -> upload(u, true) } }
    val pickFile = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { it?.let { u -> upload(u, false) } }

    LaunchedEffect(vm.messages.size) {
        if (vm.messages.isNotEmpty()) listState.animateScrollToItem(vm.messages.size - 1)
    }

    Scaffold(
        modifier = Modifier.imePadding(),
        topBar = { TopAppBar(title = { Text("传输助手") }, navigationIcon = { TextButton(onClick = onBack) { Text("返回") } }) },
        bottomBar = {
            Column(Modifier.padding(8.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    TextButton(enabled = !busy, onClick = { pickImage.launch("image/*") }) { Text("图片") }
                    TextButton(enabled = !busy, onClick = { pickFile.launch("*/*") }) { Text("文件") }
                    if (busy) Text("发送中…", Modifier.align(Alignment.CenterVertically), style = MaterialTheme.typography.bodySmall)
                }
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(value = text, onValueChange = { text = it }, modifier = Modifier.weight(1f), maxLines = 4, placeholder = { Text("发送给电脑…") })
                    Button(enabled = text.isNotBlank(), onClick = {
                        val t = text.trim()
                        text = ""
                        scope.launch {
                            try { vm.addMessage(client.chatSendText(t)) } catch (e: ApiException) { toast("发送失败：${e.message}"); text = t }
                        }
                    }) { Text("发送") }
                }
            }
        },
    ) { pad ->
        LazyColumn(
            Modifier.fillMaxSize().padding(pad).padding(horizontal = 12.dp),
            state = listState, verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(vm.messages, key = { it.seq }) { m ->
                Bubble(m, client, onSave = {
                    scope.launch {
                        try {
                            saveToDownloads(ctx, client, m)
                            toast("已保存到下载目录：${m.fileName}")
                        } catch (e: Exception) {
                            toast("保存失败：${e.message}")
                        }
                    }
                })
            }
        }
    }
}

@Composable
private fun Bubble(m: ChatMessage, client: ApiClient, onSave: () -> Unit) {
    val mine = m.from == "phone"
    Row(Modifier.fillMaxWidth(), horizontalArrangement = if (mine) Arrangement.End else Arrangement.Start) {
        Card(
            Modifier.widthIn(max = 300.dp),
            colors = CardDefaults.cardColors(
                containerColor = if (mine) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceVariant,
            ),
        ) {
            Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                when (m.type) {
                    "text" -> Text(m.text)
                    else -> {
                        if (m.type == "image" && m.size in 1..MAX_PREVIEW_BYTES) {
                            val bmp by produceState<ImageBitmap?>(null, m.id) { value = loadImage(client, m) }
                            bmp?.let { Image(it, contentDescription = m.fileName, modifier = Modifier.fillMaxWidth()) }
                        }
                        Text("${if (m.type == "image") "🖼 " else "📄 "}${m.fileName}", style = MaterialTheme.typography.bodyMedium)
                        Text(humanSize(m.size), style = MaterialTheme.typography.bodySmall)
                        TextButton(onClick = onSave) { Text(if (mine) "再次保存" else "保存到手机") }
                    }
                }
                Text(
                    DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(m.time)),
                    style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

private suspend fun loadImage(client: ApiClient, m: ChatMessage): ImageBitmap? = try {
    val out = ByteArrayOutputStream()
    client.chatDownload(m.id, out)
    val bytes = out.toByteArray()
    withContext(Dispatchers.Default) {
        android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
    }
} catch (_: Exception) {
    null
}

private fun humanSize(n: Long): String = when {
    n >= 1L shl 30 -> "%.2f GB".format(n / (1L shl 30).toDouble())
    n >= 1L shl 20 -> "%.1f MB".format(n / (1L shl 20).toDouble())
    n >= 1L shl 10 -> "%.1f KB".format(n / 1024.0)
    else -> "$n B"
}

private fun queryNameSize(ctx: Context, uri: Uri): Pair<String, Long> {
    var name = "file"
    var size = -1L
    ctx.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
        if (c.moveToFirst()) {
            c.getString(0)?.let { name = it }
            if (!c.isNull(1)) size = c.getLong(1)
        }
    }
    return name to size
}

/** 保存到公共「下载」目录：Android 10+ 走 MediaStore（无需权限），更低版本直接写文件。 */
private suspend fun saveToDownloads(ctx: Context, client: ApiClient, m: ChatMessage) {
    val name = m.fileName.ifEmpty { "file" }
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, name)
            put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/图吧工具箱")
            put(MediaStore.Downloads.IS_PENDING, 1)
        }
        val uri = ctx.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw ApiException("无法创建文件")
        try {
            ctx.contentResolver.openOutputStream(uri)!!.use { client.chatDownload(m.id, it) }
            ctx.contentResolver.update(uri, ContentValues().apply { put(MediaStore.Downloads.IS_PENDING, 0) }, null, null)
        } catch (e: Exception) {
            ctx.contentResolver.delete(uri, null, null)
            throw e
        }
    } else {
        withContext(Dispatchers.IO) {
            val dir = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "图吧工具箱").apply { mkdirs() }
            File(dir, name.replace('/', '_')).outputStream().use { client.chatDownload(m.id, it) }
        }
    }
}
