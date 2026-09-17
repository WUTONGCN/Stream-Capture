#include "pch.h"

#include <list>
#include <regex>
#include <filesystem>

#include "push-widget.h"
#include "plugin-support.h"

#include "output-config.h"

#ifdef _WIN32
#include <Windows.h>
#endif

// StreamCapture自动集成
#include <QTimer>
#include <QFileSystemWatcher>
#include <fstream>
#include "dep/nlohmann-json/json.hpp"

#define ConfigSection "obs-multi-rtmp"
#define STREAMCAPTURE_CMD_FILE "obs-multi-rtmp-streamcapture-cmd.json"

static class GlobalServiceImpl : public GlobalService
{
public:
    bool RunInUIThread(std::function<void()> task) override {
        if (uiThread_ == nullptr)
            return false;
        QMetaObject::invokeMethod(uiThread_, [func = std::move(task)]() {
            func();
        });
        return true;
    }

    QThread* uiThread_ = nullptr;
} s_service;


GlobalService& GetGlobalService() {
    return s_service;
}


class MultiOutputWidget : public QWidget
{
public:
    MultiOutputWidget(QWidget* parent = 0)
        : QWidget(parent)
    {
        setWindowTitle(obs_module_text("Title"));

        container_ = new QWidget(&scroll_);
        layout_ = new QVBoxLayout(container_);
        layout_->setAlignment(Qt::AlignmentFlag::AlignTop);

        // init widget
        auto addButton = new QPushButton(obs_module_text("Btn.NewTarget"), container_);
        QObject::connect(addButton, &QPushButton::clicked, [this]() {
            auto& global = GlobalMultiOutputConfig();
            auto newid = GenerateId(global);
            auto target = std::make_shared<OutputTargetConfig>();
            target->id = newid;
            global.targets.emplace_back(target);
            auto pushwidget = createPushWidget(newid, container_);
            itemLayout_->addWidget(pushwidget);
            if (pushwidget->ShowEditDlg())
                SaveConfig();
            else {
                auto it = std::find_if(global.targets.begin(), global.targets.end(), [newid](auto& x) {
                    return x->id == newid;
                });
                if (it != global.targets.end())
                    global.targets.erase(it);
                delete pushwidget;
            }
        });
        layout_->addWidget(addButton);

        // start all, stop all
        auto allBtnContainer = new QWidget(this);
        auto allBtnLayout = new QHBoxLayout();
        auto startAllButton = new QPushButton(obs_module_text("Btn.StartAll"), allBtnContainer);
        allBtnLayout->addWidget(startAllButton);
        auto stopAllButton = new QPushButton(obs_module_text("Btn.StopAll"), allBtnContainer);
        allBtnLayout->addWidget(stopAllButton);
        allBtnContainer->setLayout(allBtnLayout);
        layout_->addWidget(allBtnContainer);

        QObject::connect(startAllButton, &QPushButton::clicked, [this]() {
            for (auto x : GetAllPushWidgets())
                x->StartStreaming();
        });
        QObject::connect(stopAllButton, &QPushButton::clicked, [this]() {
            for (auto x : GetAllPushWidgets())
                x->StopStreaming();
        });
        
        // load config
        itemLayout_ = new QVBoxLayout(container_);
        LoadConfig();
        layout_->addLayout(itemLayout_);

        scroll_.setWidgetResizable(true);
        scroll_.setWidget(container_);

        auto fullLayout = new QGridLayout(this);
        fullLayout->setContentsMargins(0, 0, 0, 0);
        fullLayout->setRowStretch(0, 1);
        fullLayout->setColumnStretch(0, 1);
        fullLayout->addWidget(&scroll_, 0, 0);

        // 启动StreamCapture自动集成监听器
        StartStreamCaptureIntegration();
    }

    std::vector<PushWidget*> GetAllPushWidgets()
    {
        std::vector<PushWidget*> result;
        for(auto& c : container_->children())
        {
            if (c->objectName() == "push-widget")
            {
                auto w = dynamic_cast<PushWidget*>(c);
                result.push_back(w);
            }
        }
        return result;
    }

    void SaveConfig()
    {
        SaveMultiOutputConfig();
    }

    void LoadConfig()
    {
        for(auto x: GetAllPushWidgets()) {
            delete x;
        }
        GlobalMultiOutputConfig() = {};

        if (LoadMultiOutputConfig()) {
            for(auto x: GlobalMultiOutputConfig().targets)
            {
                auto pushwidget = createPushWidget(x->id, container_);
                itemLayout_->addWidget(pushwidget);
            }
        }
    }

    // StreamCapture自动集成功能
    void StartStreamCaptureIntegration()
    {
        // 创建定时器，每500ms检查一次命令文件
        streamCaptureTimer_ = new QTimer(this);
        QObject::connect(streamCaptureTimer_, &QTimer::timeout, this, &MultiOutputWidget::CheckStreamCaptureCommand);
        streamCaptureTimer_->start(500);
        
        blog(LOG_INFO, TAG "StreamCapture自动集成已启动");
    }

    void CheckStreamCaptureCommand()
    {
        // 获取配置文件路径
        std::string profilePath = obs_frontend_get_current_profile_path();
        std::string cmdFilePath = profilePath + "/" + STREAMCAPTURE_CMD_FILE;

        // 检查命令文件是否存在
        if (!std::filesystem::exists(cmdFilePath))
            return;

        try {
            // 读取命令文件
            std::ifstream file(cmdFilePath);
            if (!file.is_open())
                return;

            nlohmann::json cmdJson;
            file >> cmdJson;
            file.close();

            // 解析命令
            std::string action = cmdJson.value("action", "");
            if (action == "add_target")
            {
                std::string platform = cmdJson.value("platform", "Unknown");
                std::string server = cmdJson.value("server", "");
                std::string key = cmdJson.value("key", "");
                std::string name = cmdJson.value("name", "");  // 支持自定义名称
                bool autoStart = cmdJson.value("auto_start", false);  // 默认不自动开始

                if (!server.empty())
                {
                    AddStreamCaptureTarget(platform, server, key, name, autoStart);
                    blog(LOG_INFO, TAG "StreamCapture: 添加推流目标 [%s] %s", platform.c_str(), server.c_str());
                }
            }
            else if (action == "start_stream")
            {
                // 开始推流命令
                std::string targetName = cmdJson.value("target_name", "");
                StartStreamByName(targetName);
                blog(LOG_INFO, TAG "StreamCapture: 开始推流 [%s]", targetName.c_str());
            }

            // 处理完成后删除命令文件
            std::filesystem::remove(cmdFilePath);
        }
        catch (const std::exception& e) {
            blog(LOG_WARNING, TAG "StreamCapture命令处理失败: %s", e.what());
            // 删除损坏的命令文件
            std::filesystem::remove(cmdFilePath);
        }
    }

    // 根据名称启动推流
    void StartStreamByName(const std::string& targetName)
    {
        auto& global = GlobalMultiOutputConfig();
        auto widgets = GetAllPushWidgets();
        
        // 如果为空或"all"，启动所有
        if (targetName.empty() || targetName == "all")
        {
            for (auto x : widgets)
                x->StartStreaming();
            blog(LOG_INFO, TAG "StreamCapture: 启动所有推流目标");
            return;
        }
        
        // 按名称匹配启动
        // widgets和targets的顺序是对应的
        size_t idx = 0;
        for (auto& t : global.targets)
        {
            if (t->name == targetName && idx < widgets.size())
            {
                widgets[idx]->StartStreaming();
                blog(LOG_INFO, TAG "StreamCapture: 启动推流目标 [%s]", targetName.c_str());
                return;
            }
            idx++;
        }
        
        // 如果没找到匹配的，可能是刚添加的，启动最后一个
        if (!widgets.empty())
        {
            widgets.back()->StartStreaming();
            blog(LOG_INFO, TAG "StreamCapture: 启动最新添加的推流目标");
        }
    }

    void AddStreamCaptureTarget(const std::string& platform, const std::string& server, const std::string& key, const std::string& customName, bool autoStart)
    {
        auto& global = GlobalMultiOutputConfig();
        
        // 生成新ID
        auto newid = GenerateId(global);
        
        // 创建目标配置
        auto target = std::make_shared<OutputTargetConfig>();
        target->id = newid;
        // 使用自定义名称，如果没有则使用平台名称
        target->name = customName.empty() ? platform : customName;
        target->protocol = "RTMP";
        
        // 设置服务参数
        target->serviceParam["server"] = server;
        target->serviceParam["key"] = key;
        
        // 添加到全局配置
        global.targets.emplace_back(target);
        
        // 创建UI组件
        auto pushwidget = createPushWidget(newid, container_);
        itemLayout_->addWidget(pushwidget);
        
        // 保存配置
        SaveConfig();
        
        // 自动开始推流
        if (autoStart)
        {
            QTimer::singleShot(500, [pushwidget]() {
                pushwidget->StartStreaming();
            });
        }
        
        blog(LOG_INFO, TAG "StreamCapture: 成功添加并配置推流目标 [%s]", platform.c_str());
    }

private:
    QWidget* container_ = 0;
    QScrollArea scroll_;
    QVBoxLayout* itemLayout_ = 0;
    QVBoxLayout* layout_ = 0;
    
    // StreamCapture集成
    QTimer* streamCaptureTimer_ = nullptr;
};

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE("obs-multi-rtmp", "en-US")
OBS_MODULE_AUTHOR("雷鳴 (@sorayukinoyume)")

bool obs_module_load()
{
    auto mainwin = (QMainWindow*)obs_frontend_get_main_window();
    if (mainwin == nullptr)
        return false;
    QMetaObject::invokeMethod(mainwin, []() {
        s_service.uiThread_ = QThread::currentThread();
    });

    auto dock = new MultiOutputWidget();
    dock->setObjectName("obs-multi-rtmp-dock");
    if (!obs_frontend_add_dock_by_id("obs-multi-rtmp-dock", obs_module_text("Title"), dock))
    {
        delete dock;
        return false;
    }

    blog(LOG_INFO, TAG "version: %s by SoraYuki https://github.com/sorayuki/obs-multi-rtmp/", PLUGIN_VERSION);

    obs_frontend_add_event_callback(
        [](enum obs_frontend_event event, void *private_data) {
            auto dock = static_cast<MultiOutputWidget*>(private_data);

            for(auto x: dock->GetAllPushWidgets())
                x->OnOBSEvent(event);

            if (event == obs_frontend_event::OBS_FRONTEND_EVENT_EXIT)
            {   
                dock->SaveConfig();
            }
            else if (event == obs_frontend_event::OBS_FRONTEND_EVENT_PROFILE_CHANGED)
            {
                dock->LoadConfig();
            }
        }, dock
    );

    return true;
}

const char *obs_module_description(void)
{
    return "Multiple RTMP Output Plugin";
}
