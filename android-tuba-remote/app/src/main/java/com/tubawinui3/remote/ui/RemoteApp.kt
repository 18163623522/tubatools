package com.tubawinui3.remote.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp
import com.tubawinui3.remote.RemoteViewModel
import com.tubawinui3.remote.data.MonitorData
import java.util.Locale

private enum class Tool(val title: String, val subtitle: String) {
    SHELL("PowerShell 终端", "在电脑上执行命令并查看输出"),
    WINGET("应用安装", "搜索并通过 winget 在电脑上安装软件"),
    CHAT("传输助手", "与电脑互发文字、图片和文件"),
}

@Composable
fun RemoteApp(vm: RemoteViewModel) {
    var tool by rememberSaveable { mutableStateOf<String?>(null) }
    val client = vm.client

    if (client == null) {
        // 未连接：背景占位 + 连接面板
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            if (vm.connecting) CircularProgressIndicator()
            else Text("图吧工具箱手机助手", style = MaterialTheme.typography.titleLarge)
        }
        ConnectSheet(vm)
        return
    }

    val current = tool?.let { runCatching { Tool.valueOf(it) }.getOrNull() }
    if (current != null) {
        BackHandler { tool = null }
        when (current) {
            Tool.SHELL -> ShellScreen(client, onBack = { tool = null })
            Tool.WINGET -> WingetScreen(client, onBack = { tool = null })
            Tool.CHAT -> ChatScreen(vm, client, onBack = { tool = null })
        }
        return
    }
    Dashboard(vm, onOpen = { tool = it.name })
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun Dashboard(vm: RemoteViewModel, onOpen: (Tool) -> Unit) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(vm.hostName.ifEmpty { "已连接" }) },
                actions = { TextButton(onClick = { vm.disconnect() }) { Text("断开") } },
            )
        },
    ) { pad ->
        LazyColumn(
            Modifier.fillMaxSize().padding(pad),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item { MonitorCard(vm.monitor) }
            item { ScreenshotCard(vm) }
            item { Text("小工具", style = MaterialTheme.typography.titleMedium) }
            Tool.entries.forEach { t ->
                item {
                    Card(Modifier.fillMaxWidth().clickable { onOpen(t) }) {
                        Column(Modifier.padding(16.dp)) {
                            Text(t.title, style = MaterialTheme.typography.titleMedium)
                            Text(t.subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }
}

private fun fmt(v: Double, unit: String, digits: Int = 0): String =
    if (v < 0) "—" else String.format(Locale.US, "%.${digits}f", v) + unit

@Composable
private fun MonitorCard(m: MonitorData?) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("硬件监控", style = MaterialTheme.typography.titleMedium)
            if (m == null) {
                LinearProgressIndicator(Modifier.fillMaxWidth())
                return@Column
            }
            Stat("CPU", m.cpuName, m.cpuLoad, "${fmt(m.cpuLoad, "%")}  ${fmt(m.cpuTemp, "℃")}  ${fmt(m.cpuClock / 1000, " GHz", 2)}  ${fmt(m.cpuPower, " W")}")
            if (m.gpuName.isNotEmpty() || m.gpuLoad >= 0)
                Stat("GPU", m.gpuName, m.gpuLoad, "${fmt(m.gpuLoad, "%")}  ${fmt(m.gpuTemp, "℃")}  ${fmt(m.gpuVramUsedGB, " GB", 1)} 显存  ${fmt(m.gpuPower, " W")}")
            Stat("内存", "", m.memLoad, "${fmt(m.memUsedGB, "", 1)} / ${fmt(m.memTotalGB, " GB", 1)}（${fmt(m.memLoad, "%")}）")
            Text(
                "磁盘 读 ${fmt(m.diskReadMBs, " MB/s", 1)} 写 ${fmt(m.diskWriteMBs, " MB/s", 1)}  ·  网络 ↑${fmt(m.netUpMBs, " MB/s", 2)} ↓${fmt(m.netDownMBs, " MB/s", 2)}",
                style = MaterialTheme.typography.bodySmall,
            )
            if (m.batPercent >= 0) Text("电池 ${fmt(m.batPercent, "%")}${if (m.batCharging) "（充电中）" else ""}", style = MaterialTheme.typography.bodySmall)
            if (m.fps >= 0) Text("帧率 ${fmt(m.fps, " FPS")}", style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
private fun Stat(label: String, name: String, load: Double, detail: String) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(if (name.isEmpty()) label else "$label  $name", style = MaterialTheme.typography.labelLarge, maxLines = 1, modifier = Modifier.weight(1f))
        }
        if (load >= 0) LinearProgressIndicator(progress = { (load / 100).toFloat().coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
        Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun ScreenshotCard(vm: RemoteViewModel) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
                Text("电脑屏幕", style = MaterialTheme.typography.titleMedium)
                TextButton(onClick = { vm.refreshScreenshot() }, enabled = !vm.screenshotLoading) {
                    Text(if (vm.screenshotLoading) "刷新中…" else "刷新截图")
                }
            }
            val img = vm.screenshot
            if (img != null) {
                Image(img, contentDescription = "电脑屏幕截图", modifier = Modifier.fillMaxWidth(), contentScale = ContentScale.FillWidth)
            } else if (vm.screenshotLoading) {
                LinearProgressIndicator(Modifier.fillMaxWidth())
            }
            vm.screenshotError?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        }
    }
}
