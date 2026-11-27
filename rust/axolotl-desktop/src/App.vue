<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

type Page = "overview" | "instances" | "logdoctor" | "conflicts";

type Instance = {
  id: string;
  name: string;
  path: string;
  resolved_path: string;
  game_version: string;
  loader: string;
  loader_version: string;
  install_stage: string;
  last_played?: string | null;
};

type LogIssue = {
  rule: string;
  severity: string;
  title: string;
  explanation: string;
  culprit: string;
  fix: string;
  evidence: string;
  count: number;
};

type LogNoise = { count: number; signature: string; sample: string };

type Conflict = {
  kind: string;
  title: string;
  detail: string;
  mods: string[];
  evidence: string;
};

type LogReport = {
  summary: string;
  total_lines: number;
  noise_lines: number;
  issues: LogIssue[];
  noise: LogNoise[];
};

const page = ref<Page>("overview");
const backend = ref("连接中…");
const backendOk = ref(false);
const instances = ref<Instance[]>([]);
const selectedId = ref("");
const loading = ref(false);
const message = ref("");
const report = ref<LogReport | null>(null);
const conflicts = ref<Conflict[]>([]);
const conflictScan = ref(false);

const selected = computed(() => instances.value.find((item) => item.id === selectedId.value));
const nav = [
  { id: "overview" as Page, label: "总览", icon: "M3 10.5 12 3l9 7.5V21H3z" },
  { id: "instances" as Page, label: "实例", icon: "M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z" },
  { id: "logdoctor" as Page, label: "Log Doctor", icon: "M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1" },
  { id: "conflicts" as Page, label: "冲突", icon: "M5 21V4m0 1h11l-2 4 2 4H5" },
];
const appWindow = getCurrentWindow();
const current = computed(() => nav.find((item) => item.id === page.value)!);

onMounted(async () => {
  try {
    backend.value = await invoke<string>("ping");
    backendOk.value = true;
  } catch (error) {
    backend.value = String(error);
    backendOk.value = false;
  }
});

function minimizeWindow() { appWindow.minimize(); }
function toggleMaximizeWindow() { appWindow.toggleMaximize(); }
function closeWindow() { appWindow.close(); }

async function loadInstances() {
  loading.value = true;
  message.value = "";
  try {
    instances.value = await invoke<Instance[]>("list_instances");
    if (!selectedId.value && instances.value.length) selectedId.value = instances.value[0].id;
    message.value = `已读取 ${instances.value.length} 个 Axolotl 实例`;
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function runLogDoctor() {
  if (!selectedId.value) {
    await loadInstances();
    if (!selectedId.value) return;
  }
  loading.value = true;
  message.value = "";
  try {
    report.value = await invoke<LogReport>("analyze_log", { instanceId: selectedId.value });
    message.value = report.value.summary;
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function scanConflicts() {
  if (!selectedId.value) await loadInstances();
  if (!selectedId.value) return;
  loading.value = true;
  conflictScan.value = false;
  message.value = "";
  try {
    conflicts.value = await invoke<Conflict[]>("scan_conflicts", { instanceId: selectedId.value });
    conflictScan.value = true;
    message.value = `扫描完成：${conflicts.value.length} 个冲突`;
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function launch() {
  if (!selectedId.value) return;
  loading.value = true;
  try {
    const result = await invoke<Record<string, unknown>>("launch_instance", {
      instanceId: selectedId.value,
    });
    message.value = `启动请求已提交：${JSON.stringify(result)}`;
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function backup() {
  if (!selectedId.value) return;
  loading.value = true;
  try {
    const result = await invoke<Record<string, unknown>>("backup_instance", {
      instanceId: selectedId.value,
    });
    message.value = `备份请求已提交：${JSON.stringify(result)}`;
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

function openLogDoctor(id: string) {
  selectedId.value = id;
  page.value = "logdoctor";
  report.value = null;
}
</script>

<template>
  <div class="window">
    <header class="titlebar" data-tauri-drag-region>
      <div class="titlebar-brand" data-tauri-drag-region>
        <img src="/app.png" alt="" />
        <span>Axolotl Companion</span>
      </div>
      <div class="window-controls">
        <button title="最小化" @click="minimizeWindow">—</button>
        <button title="最大化" @click="toggleMaximizeWindow">□</button>
        <button title="关闭" @click="closeWindow">×</button>
      </div>
    </header>

    <div class="app">
    <aside class="rail">
      <div class="brand"><img src="/app.png" alt="" /></div>
      <button
        v-for="item in nav"
        :key="item.id"
        class="rail-button"
        :class="{ active: page === item.id }"
        :title="item.label"
        @click="page = item.id"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path :d="item.icon" />
        </svg>
      </button>
      <div class="rail-status">
        <span class="dot" :class="{ ok: backendOk }"></span>
      </div>
    </aside>

    <main class="workspace">
      <section class="surface">
        <header class="header">
          <div class="header-title">
            <span class="header-icon">
              <svg viewBox="0 0 24 24" aria-hidden="true"><path :d="current.icon" /></svg>
            </span>
            <div>
              <h1>{{ current.label }}</h1>
              <p>Axolotl 的依赖诊断与日志分析附加工具</p>
            </div>
          </div>
          <div class="header-actions">
            <button
              v-if="page === 'overview' || page === 'instances'"
              class="button secondary"
              :disabled="loading"
              @click="loadInstances"
            >
              {{ loading ? "读取中…" : page === "overview" ? "读取实例" : "刷新" }}
            </button>
            <button
              v-if="page === 'logdoctor'"
              class="button primary"
              :disabled="loading"
              @click="runLogDoctor"
            >
              {{ loading ? "分析中…" : "分析日志" }}
            </button>
            <button
              v-if="page === 'conflicts'"
              class="button primary"
              :disabled="loading"
              @click="scanConflicts"
            >
              {{ loading ? "扫描中…" : "扫描冲突" }}
            </button>
          </div>
        </header>

        <div class="body">
          <p v-if="message" class="banner">{{ message }}</p>

          <template v-if="page === 'overview'">
            <div class="status-line">
              <span class="dot" :class="{ ok: backendOk }"></span>
              <span>{{ backendOk ? "Axolotl MCP 已连接" : "Axolotl MCP 未连接" }}</span>
            </div>

            <div v-if="instances.length || report" class="stats">
              <article class="panel stat">
                <span>Axolotl 实例</span>
                <strong>{{ instances.length }}</strong>
                <button class="text-button" :disabled="loading" @click="loadInstances">
                  读取实例
                </button>
              </article>
              <article class="panel stat">
                <span>日志问题</span>
                <strong>{{ report?.issues.length ?? 0 }}</strong>
                <button class="text-button" @click="page = 'logdoctor'">打开 Log Doctor</button>
              </article>
              <article class="panel stat">
                <span>隔离噪声</span>
                <strong>{{ report?.noise_lines ?? 0 }}</strong>
                <span class="stat-note">重复低风险日志行</span>
              </article>
            </div>

            <div class="section-head">
              <h2>工具</h2>
            </div>
            <div class="tools">
              <button class="panel tool" @click="page = 'logdoctor'">
                <span class="tool-icon violet">✦</span>
                <div>
                  <strong>Log Doctor</strong>
                  <p>翻译日志、隔离噪声、指出可疑模组。</p>
                </div>
              </button>
              <button class="panel tool" @click="page = 'conflicts'">
                <span class="tool-icon blue">⚑</span>
                <div>
                  <strong>冲突实验室</strong>
                  <p>重复类、Mixin 目标和 Access Transformer。</p>
                </div>
              </button>
              <button class="panel tool" @click="page = 'instances'">
                <span class="tool-icon green">▦</span>
                <div>
                  <strong>实例分析</strong>
                  <p>只读 Axolotl 实例，定位日志与依赖。</p>
                </div>
              </button>
            </div>

            <template v-if="instances.length">
              <div class="section-head">
                <h2>最近实例</h2>
                <span>{{ instances.length }} 个</span>
              </div>
              <div class="panel list">
                <button
                  v-for="item in instances.slice(0, 3)"
                  :key="item.id"
                  class="row-button"
                  @click="openLogDoctor(item.id)"
                >
                  <div>
                    <strong>{{ item.name }}</strong>
                    <p>{{ item.game_version }} · {{ item.loader }} {{ item.loader_version }}</p>
                  </div>
                  <span class="tag">{{ item.install_stage }}</span>
                </button>
              </div>
            </template>
          </template>

          <template v-else-if="page === 'instances'">
            <div class="status-line">
              <span>{{ instances.length }} 个 Axolotl 实例</span>
            </div>
            <p v-if="!instances.length" class="empty-text">
              还没有读取实例。点右上角“读取实例”，Axolotl 会请求一次授权。
            </p>
            <div v-for="item in instances" :key="item.id" class="panel instance">
              <div class="instance-info">
                <strong>{{ item.name }}</strong>
                <p>{{ item.game_version }} · {{ item.loader }} {{ item.loader_version }}</p>
                <code>{{ item.resolved_path }}</code>
              </div>
              <div class="instance-actions">
                <button class="button secondary" @click="openLogDoctor(item.id)">Log Doctor</button>
                <button class="button secondary" :disabled="loading" @click="selectedId = item.id; backup()">
                  备份
                </button>
                <button class="button primary" :disabled="loading" @click="selectedId = item.id; launch()">
                  启动
                </button>
              </div>
            </div>
          </template>

          <template v-else-if="page === 'logdoctor'">
            <div class="panel toolbar">
              <select v-model="selectedId">
                <option value="" disabled>选择实例</option>
                <option v-for="item in instances" :key="item.id" :value="item.id">
                  {{ item.name }}
                </option>
              </select>
              <button class="button secondary" :disabled="loading" @click="loadInstances">读取实例</button>
            </div>

            <template v-if="report">
              <div class="panel verdict">
                <span class="eyebrow">诊断结论</span>
                <h2>{{ report.issues.length ? report.issues[0].title : "没有发现明确错误" }}</h2>
                <p>{{ report.summary }}</p>
                <div class="chips">
                  <span class="tag">{{ report.total_lines }} 行日志</span>
                  <span class="tag">{{ report.issues.length }} 个问题</span>
                  <span class="tag">{{ report.noise_lines }} 行已隔离噪声</span>
                </div>
              </div>

              <article v-for="issue in report.issues" :key="issue.rule + issue.evidence" class="panel issue">
                <div class="issue-head">
                  <span class="severity" :class="issue.severity">{{ issue.severity }} ×{{ issue.count }}</span>
                  <h3>{{ issue.title }}</h3>
                  <span v-if="issue.culprit" class="source-chip">可能相关：{{ issue.culprit }}</span>
                </div>
                <div class="issue-block">
                  <span>发生了什么</span>
                  <p>{{ issue.explanation }}</p>
                </div>
                <div class="issue-block next">
                  <span>下一步</span>
                  <p>{{ issue.fix }}</p>
                </div>
                <details class="evidence">
                  <summary>查看原始证据</summary>
                  <pre>{{ issue.evidence }}</pre>
                </details>
              </article>

              <details v-if="report.noise.length" class="panel noise">
                <summary>已隔离 {{ report.noise_lines }} 行重复噪声（展开）</summary>
                <div v-for="item in report.noise" :key="item.signature" class="noise-row">
                  <strong>{{ item.count }}×</strong>
                  <code>{{ item.signature }}</code>
                </div>
              </details>
            </template>
            <div v-else class="panel empty">
              <h2>选择一个实例开始</h2>
              <p>点右上角“分析日志”。只读 logs/latest.log，不会修改实例，也不会启动游戏。</p>
            </div>
          </template>

          <template v-else>
            <div class="panel toolbar">
              <select v-model="selectedId">
                <option value="" disabled>选择实例</option>
                <option v-for="item in instances" :key="item.id" :value="item.id">
                  {{ item.name }}
                </option>
              </select>
              <button class="button secondary" :disabled="loading" @click="loadInstances">读取实例</button>
            </div>
            <div v-if="!conflictScan" class="panel empty">
              <h2>扫描 JAR 与 Mixin 目标</h2>
              <p>读取实例 mods 目录，检查重复类、Mixin 目标重叠和 Access Transformer 冲突。</p>
            </div>
            <template v-else-if="conflicts.length === 0">
              <div class="panel empty">
                <h2>没有发现字节码冲突</h2>
                <p>只比较了真实 class 注解和 AT 行；不代表没有依赖/版本问题。</p>
              </div>
            </template>
            <article v-for="item in conflicts" :key="item.kind + item.evidence" class="panel issue">
              <div class="issue-head">
                <span class="severity warning">{{ item.kind }}</span>
                <h3>{{ item.title }}</h3>
              </div>
              <p>{{ item.detail }}</p>
              <div class="source-chip">涉及：{{ item.mods.join("、") }}</div>
              <details class="evidence">
                <summary>查看证据</summary>
                <pre>{{ item.evidence }}</pre>
              </details>
            </article>
          </template>
        </div>
      </section>
    </main>
    </div>
  </div>
</template>
