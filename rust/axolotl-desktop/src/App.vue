<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { invoke } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

type Page = "instances" | "logdoctor" | "conflicts" | "performance";

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
  confidence: string;
  fix: string;
  evidence: string;
  targets: string[];
  count: number;
};

type LogNoise = { count: number; signature: string; sample: string };

type LogReport = {
  summary: string;
  total_lines: number;
  noise_lines: number;
  issues: LogIssue[];
  noise: LogNoise[];
};

type Conflict = {
  kind: string;
  title: string;
  detail: string;
  mods: string[];
  evidence: string;
  sources: string[];
  recommendation: string;
};

type PerfEntry = {
  name: string;
  detail: string;
  milliseconds: number;
  percent?: number | null;
};

type ProfilerStatus = {
  spark_installed: boolean;
  caliper_available: boolean;
  report_found: boolean;
  report_path?: string | null;
  guidance: string[];
};

type PerformanceReport = {
  source: string;
  kind: string;
  summary: string;
  entries: PerfEntry[];
  notes: string[];
};

const page = ref<Page>("instances");
const backendOk = ref(false);
const instances = ref<Instance[]>([]);
const selectedId = ref("");
const loading = ref(false);
const message = ref("");
const report = ref<LogReport | null>(null);
const conflicts = ref<Conflict[]>([]);
const conflictScan = ref(false);
const perfReport = ref<PerformanceReport | null>(null);
const profilerStatus = ref<ProfilerStatus | null>(null);
const perfFile = ref<HTMLInputElement | null>(null);
const bodyEl = ref<HTMLElement | null>(null);

const selected = computed(() => instances.value.find((item) => item.id === selectedId.value));
const isLocal = computed(() => selected.value?.id.startsWith("path:") ?? false);
const localPath = ref("");
const nav = [
  { id: "instances" as Page, label: "实例", icon: "M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z" },
  { id: "logdoctor" as Page, label: "Log Doctor", icon: "M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1" },
  { id: "conflicts" as Page, label: "冲突", icon: "M5 21V4m0 1h11l-2 4 2 4H5" },
  { id: "performance" as Page, label: "性能", icon: "M4 19V5M4 19h16M8 15l3-4 3 2 4-6" },
];
const current = computed(() => nav.find((item) => item.id === page.value)!);
const perfMax = computed(() =>
  Math.max(1, ...(perfReport.value?.entries.map((item) => item.milliseconds) ?? [1])),
);
const conflictGroups = computed(() => {
  const groups: Record<string, number> = {};
  for (const item of conflicts.value) groups[item.kind] = (groups[item.kind] ?? 0) + 1;
  return groups;
});
const conflictGroupList = computed(() => {
  const groups: Record<string, Conflict[]> = {};
  for (const item of conflicts.value) (groups[item.kind] ??= []).push(item);
  return Object.entries(groups).map(([kind, items]) => ({
    kind,
    label: kindLabel[kind] ?? kind,
    items,
    recommendation: items[0]?.recommendation ?? "",
  }));
});

function conflictTarget(item: Conflict) {
  const line = item.evidence
    .split("\n")
    .find((value) => value.startsWith("target:") || value.startsWith("class:"));
  return line ? line.replace(/^(target|class):\s*/, "") : item.title;
}
const appWindow = getCurrentWindow();

watch(page, async () => {
  await nextTick();
  bodyEl.value?.scrollTo({ top: 0 });
});

onMounted(() => {
  invoke<string>("ping")
    .then(() => {
      backendOk.value = true;
      return loadInstances();
    })
    .catch(() => (backendOk.value = false));
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
    message.value = instances.value.length ? "" : "Axolotl 没有返回实例。";
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

function loaderInitial(loader: string) {
  const value = loader.toLowerCase();
  if (value.includes("neo")) return "N";
  if (value.includes("forge")) return "F";
  if (value.includes("fabric")) return "Fa";
  if (value.includes("quilt")) return "Q";
  return "?";
}

function selectInstance(id: string) {
  selectedId.value = id;
  report.value = null;
  conflicts.value = [];
  conflictScan.value = false;
}

function go(next: Page, id?: string) {
  if (id) selectInstance(id);
  page.value = next;
  if (next === "performance") void loadProfilerStatus();
}

async function loadProfilerStatus() {
  if (!selectedId.value) await loadInstances();
  if (!selectedId.value) return;
  try {
    profilerStatus.value = await invoke<ProfilerStatus>("profiler_status", {
      instanceId: selectedId.value,
    });
  } catch (error) {
    message.value = String(error);
  }
}

async function runLogDoctor() {
  if (!selectedId.value) await loadInstances();
  if (!selectedId.value) return;
  loading.value = true;
  message.value = "";
  try {
    report.value = await invoke<LogReport>("analyze_log", { instanceId: selectedId.value });
    message.value = "";
    await nextTick();
    bodyEl.value?.scrollTo({ top: 0 });
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
    message.value = conflicts.value.length ? "" : "没有发现字节码冲突。";
    await nextTick();
    bodyEl.value?.scrollTo({ top: 0 });
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function importPerformance(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;
  loading.value = true;
  try {
    const text = await file.text();
    const kind = file.name.toLowerCase().includes("caliper") ? "Caliper" : "Spark";
    perfReport.value = await invoke<PerformanceReport>("analyze_performance_text", {
      text,
      source: file.name,
      kind,
    });
    await nextTick();
    bodyEl.value?.scrollTo({ top: 0 });
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
    input.value = "";
  }
}

async function runPerformance() {
  if (!selectedId.value) return;
  loading.value = true;
  message.value = "";
  try {
    perfReport.value = await invoke<PerformanceReport>("analyze_performance", {
      instanceId: selectedId.value,
    });
    await nextTick();
    bodyEl.value?.scrollTo({ top: 0 });
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}

async function addLocal() {
  if (!localPath.value.trim()) return;
  loading.value = true;
  try {
    const instance = await invoke<Instance>("add_local_instance", { path: localPath.value.trim() });
    if (!instances.value.some((item) => item.id === instance.id)) instances.value.push(instance);
    selectInstance(instance.id);
    message.value = "已添加本地目录，只读分析可用；启动仍走 Axolotl。";
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
    await invoke("launch_instance", { instanceId: selectedId.value });
    message.value = "启动请求已提交。";
  } catch (error) {
    message.value = String(error);
  } finally {
    loading.value = false;
  }
}


const kindLabel: Record<string, string> = {
  "access-transformer": "AT",
  mixin: "Mixin",
  "duplicate-class": "重复类",
};
</script>

<template>
  <div class="window">
    <header class="titlebar" data-tauri-drag-region>
      <div class="titlebar-brand" data-tauri-drag-region>
        <img src="/app.png" alt="" />
        <span>Axolotl Companion</span>
      </div>
      <div class="window-controls">
        <button title="最小化" @click="minimizeWindow">
          <svg viewBox="0 0 24 24"><path d="M5 12h14" /></svg>
        </button>
        <button title="最大化" @click="toggleMaximizeWindow">
          <svg viewBox="0 0 24 24"><rect x="6" y="6" width="12" height="12" rx="1" /></svg>
        </button>
        <button title="关闭" @click="closeWindow">
          <svg viewBox="0 0 24 24"><path d="m7 7 10 10M17 7 7 17" /></svg>
        </button>
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
          @click="go(item.id)"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path :d="item.icon" /></svg>
        </button>
        <span class="rail-dot" :class="{ ok: backendOk }"></span>
      </aside>

      <main class="workspace">
        <section class="surface">
          <header class="header">
            <div class="header-title">
              <span class="header-icon">
                <svg viewBox="0 0 24 24"><path :d="current.icon" /></svg>
              </span>
              <div>
                <h1>{{ current.label }}</h1>
                <p v-if="selected">{{ selected.name }} · {{ selected.game_version }} · {{ selected.loader }}</p>
                <p v-else>选择 Axolotl 实例后开始</p>
              </div>
            </div>
            <div class="header-actions">
              <button v-if="page === 'instances'" class="button secondary" :disabled="loading" @click="loadInstances">
                {{ loading ? "读取中…" : "刷新" }}
              </button>
              <button v-if="page === 'logdoctor'" class="button primary" :disabled="loading || !selectedId" @click="runLogDoctor">
                {{ loading ? "分析中…" : "分析日志" }}
              </button>
              <button v-if="page === 'conflicts'" class="button primary" :disabled="loading || !selectedId" @click="scanConflicts">
                {{ loading ? "扫描中…" : "扫描冲突" }}
              </button>
              <button v-if="page === 'performance'" class="button primary" :disabled="loading || !selectedId" @click="runPerformance">
                {{ loading ? "分析中…" : "分析报告" }}
              </button>
            </div>
          </header>

          <div ref="bodyEl" class="body">
            <p v-if="message" class="banner">{{ message }}</p>

            <template v-if="page === 'instances'">
              <div v-if="!instances.length" class="connect">
                <h2>连接 Axolotl 实例</h2>
                <p>第一次读取会在 Axolotl 里请求一次授权，之后会话复用。</p>
                <button class="button primary" :disabled="loading" @click="loadInstances">读取实例</button>
                <div class="local-open">
                  <span>或直接打开本地实例目录：</span>
                  <input v-model="localPath" placeholder="E:\\...\\minecraft\\versions\\..." />
                  <button class="button secondary" :disabled="loading" @click="addLocal">打开</button>
                </div>
              </div>

              <template v-else>
                <div class="instance-list">
                  <button
                    v-for="item in instances"
                    :key="item.id"
                    class="instance-row"
                    :class="{ active: item.id === selectedId }"
                    @click="selectInstance(item.id)"
                  >
                    <div class="instance-leading">
                      <span class="instance-badge" :class="item.loader">{{ loaderInitial(item.loader) }}</span>
                      <div class="instance-name">
                        <strong>{{ item.name }}</strong>
                        <span>{{ item.game_version }} · {{ item.loader }} {{ item.loader_version }}</span>
                      </div>
                    </div>
                    <span class="tag">{{ item.install_stage }}</span>
                    <span v-if="item.id === selectedId" class="selected-mark">已选择</span>
                  </button>
                </div>

                <div v-if="selected" class="instance-detail">
                  <div>
                    <span class="label">实例目录</span>
                    <code>{{ selected.resolved_path }}</code>
                  </div>
                  <div class="detail-actions">
                    <button class="button secondary" @click="go('logdoctor')">Log Doctor</button>
                    <button class="button secondary" @click="go('conflicts')">扫描冲突</button>
                    <span v-if="isLocal" class="tag">本地只读</span>
                    <button v-else class="button primary" :disabled="loading" @click="launch">启动游戏</button>
                  </div>
                </div>
                <div class="local-open">
                  <span>本地目录：</span>
                  <input v-model="localPath" placeholder="E:\\...\\minecraft\\versions\\..." />
                  <button class="button secondary" :disabled="loading" @click="addLocal">打开</button>
                </div>
              </template>
            </template>

            <template v-else-if="page === 'logdoctor'">
              <div v-if="!selectedId" class="connect">
                <h2>先选择实例</h2>
                <button class="button primary" @click="go('instances')">去实例页</button>
              </div>
              <template v-else>
                <div v-if="!report" class="connect">
                  <h2>分析 {{ selected?.name }} 的 latest.log</h2>
                  <p>只读日志，不修改实例，也不会启动游戏。</p>
                  <button class="button primary" :disabled="loading" @click="runLogDoctor">开始分析</button>
                </div>

                <template v-else>
                  <div class="verdict">
                    <span class="verdict-icon" :class="{ ok: !report.issues.length }">{{ report.issues.length ? "!" : "✓" }}</span>
                    <div class="verdict-copy">
                      <span class="label">诊断结论</span>
                      <h2>{{ report.issues.length ? report.issues[0].title : "没有发现明确错误" }}</h2>
                      <p>{{ report.summary }}</p>
                      <div class="chips">
                        <span class="tag">{{ report.total_lines }} 行日志</span>
                        <span class="tag">{{ report.issues.length }} 类问题</span>
                        <span class="tag">{{ report.noise_lines }} 行已隔离噪声</span>
                      </div>
                    </div>
                  </div>

                  <article v-for="issue in report.issues" :key="issue.rule" class="issue">
                    <div class="issue-head">
                      <span class="severity" :class="issue.severity">{{ issue.severity }}</span>
                      <h3>{{ issue.title }}</h3>
                      <span class="count">×{{ issue.count }}</span>
                    </div>
                    <p v-if="issue.culprit" class="issue-source">
                      可能相关：{{ issue.culprit }}
                      <span class="confidence">{{ issue.confidence }}</span>
                    </p>
                    <div v-if="issue.targets.length" class="target-list">
                      <span v-for="target in issue.targets.slice(0, 12)" :key="target" class="target-chip">{{ target }}</span>
                      <span v-if="issue.targets.length > 12" class="target-more">+{{ issue.targets.length - 12 }} 个</span>
                    </div>
                    <div class="issue-grid">
                      <div class="issue-explain">
                        <span class="label">发生了什么</span>
                        <p>{{ issue.explanation }}</p>
                      </div>
                      <div class="next">
                        <span class="label">下一步</span>
                        <p>{{ issue.fix }}</p>
                      </div>
                    </div>
                    <details>
                      <summary>原始证据</summary>
                      <pre>{{ issue.evidence }}</pre>
                    </details>
                  </article>

                  <details v-if="report.noise.length" class="noise">
                    <summary>已隔离 {{ report.noise_lines }} 行重复噪声</summary>
                    <div v-for="item in report.noise" :key="item.signature" class="noise-row">
                      <strong>{{ item.count }}×</strong>
                      <code>{{ item.signature }}</code>
                    </div>
                  </details>
                </template>
              </template>
            </template>

            <template v-else-if="page === 'performance'">
              <div v-if="!selectedId" class="connect">
                <h2>先选择实例</h2>
                <button class="button primary" @click="go('instances')">去实例页</button>
              </div>
              <template v-else>
                <div v-if="!perfReport" class="connect">
                  <h2>生成性能报告</h2>
                  <p v-if="profilerStatus">
                    Spark：{{ profilerStatus.spark_installed ? "已安装" : "未安装" }} ·
                    Caliper：不是模组，需要 JVM agent
                  </p>
                  <ul v-if="profilerStatus" class="guidance">
                    <li v-for="line in profilerStatus.guidance" :key="line">{{ line }}</li>
                  </ul>
                  <div class="actions">
                    <button v-if="profilerStatus?.spark_installed" class="button secondary" @click="launch">启动游戏</button>
                    <button class="button primary" :disabled="loading" @click="runPerformance">扫描报告</button>
                    <button class="button secondary" :disabled="loading" @click="perfFile?.click()">导入报告…</button>
                    <input ref="perfFile" type="file" accept=".json,.csv,.txt" hidden @change="importPerformance" />
                  </div>
                </div>
                <template v-else>
                  <div class="verdict">
                    <span class="verdict-icon">◔</span>
                    <div class="verdict-copy">
                      <span class="label">{{ perfReport.kind }} 归因</span>
                      <h2>{{ perfReport.summary }}</h2>
                      <p class="perf-source">{{ perfReport.source }}</p>
                      <div class="chips">
                        <span v-for="note in perfReport.notes" :key="note" class="tag">{{ note }}</span>
                      </div>
                    </div>
                  </div>

                  <div class="section-head">
                    <h2>热点 / 加载耗时</h2>
                    <button class="button secondary" :disabled="loading" @click="perfFile?.click()">导入其他报告…</button>
                  </div>
                  <input ref="perfFile" type="file" accept=".json,.csv,.txt" hidden @change="importPerformance" />
                  <div class="perf-list">
                    <div v-for="entry in perfReport.entries" :key="entry.name + entry.detail" class="perf-row">
                      <div class="perf-name">
                        <strong>{{ entry.name }}</strong>
                        <span v-if="entry.detail">{{ entry.detail }}</span>
                      </div>
                      <div class="perf-bar"><i :style="{ width: `${Math.max(3, (entry.milliseconds / perfMax) * 100)}%` }"></i></div>
                      <span class="perf-value">
                        {{ entry.percent != null ? `${entry.percent.toFixed(1)}%` : `${entry.milliseconds.toFixed(1)} ms` }}
                      </span>
                    </div>
                  </div>
                  <p class="perf-disclaimer">采样只说明“时间花在哪里”，不自动等于“删掉这个模组就更快”。</p>
                </template>
              </template>
            </template>

            <template v-else>
              <div v-if="!selectedId" class="connect">
                <h2>先选择实例</h2>
                <button class="button primary" @click="go('instances')">去实例页</button>
              </div>
              <template v-else>
                <div v-if="!conflictScan" class="connect">
                  <h2>扫描 {{ selected?.name }} 的 Jar / Mixin</h2>
                  <p>检查重复类、Mixin 目标重叠和访问级别不同的 Access Transformer。</p>
                  <button class="button primary" :disabled="loading" @click="scanConflicts">开始扫描</button>
                </div>

                <template v-else-if="conflicts.length">
                  <div class="verdict">
                    <span class="verdict-icon">!</span>
                    <div class="verdict-copy">
                    <span class="label">扫描结果</span>
                    <h2>{{ conflicts.length }} 个冲突</h2>
                    <div class="chips">
                      <span v-for="(count, kind) in conflictGroups" :key="kind" class="tag">
                        {{ kindLabel[kind] ?? kind }} {{ count }}
                      </span>
                    </div>
                    </div>
                  </div>

                  <section v-for="group in conflictGroupList" :key="group.kind" class="conflict-group">
                    <div class="section-head">
                      <h2>{{ group.label }}</h2>
                      <span>{{ group.items.length }} 处</span>
                    </div>
                    <div class="conflict-advice">
                      <span class="label">处理建议</span>
                      <p>{{ group.recommendation }}</p>
                    </div>
                    <details v-for="item in group.items" :key="item.kind + item.evidence" class="conflict-row">
                      <summary>
                        <strong>{{ conflictTarget(item) }}</strong>
                        <span>{{ item.mods.join(" + ") }}</span>
                        <em>详情</em>
                      </summary>
                      <div class="conflict-detail">
                        <p>{{ item.detail }}</p>
                        <ul v-if="item.sources.length" class="conflict-sources">
                          <li v-for="source in item.sources" :key="source">{{ source }}</li>
                        </ul>
                        <details>
                          <summary>原始证据</summary>
                          <pre>{{ item.evidence }}</pre>
                        </details>
                      </div>
                    </details>
                  </section>
                </template>

                <div v-else class="connect">
                  <h2>没有发现字节码冲突</h2>
                  <p>只检查了 JAR 中的真实 class 注解和 AT 行，不代表没有依赖或版本问题。</p>
                </div>
              </template>
            </template>
          </div>
        </section>
      </main>
    </div>
  </div>
</template>
