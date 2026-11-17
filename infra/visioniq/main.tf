terraform {
  required_version = ">= 1.6.0"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = ">= 5.30, < 8.0"
    }
  }

  # A separately approved, centrally locked state bucket is required.
  # terraform init -backend-config="bucket=<approved-terraform-state-bucket>"
  # Do not use the evidence bucket as Terraform state by default.
  backend "gcs" {
    prefix = "visioniq/dev/event-infrastructure"
  }
}

provider "google" {
  project = var.project_id
  region  = var.region
}

locals {
  topology = jsondecode(file("${path.module}/../../contracts/events/topology.json"))
  flows    = { for event in local.topology.events : event.subscription => event }

  # Topic ownership remains with the existing corporate/terraform infrastructure.
  topic_names = toset(flatten([
    for event in local.topology.events : [event.topic, event.dead_letter_topic]
  ]))

  schedules = {
    for item in jsondecode(file("${path.module}/schedules.json")).jobs :
    item.name => item
  }

  endpoints = {
    api          = trimsuffix(data.google_cloud_run_v2_service.api.uri, "/")
    intelligence = trimsuffix(data.google_cloud_run_v2_service.intelligence.uri, "/")
  }

  pubsub_service_agent = "service-${data.google_project.current.number}@gcp-sa-pubsub.iam.gserviceaccount.com"
}

data "google_project" "current" {
  project_id = var.project_id
}

data "google_pubsub_topic" "existing" {
  for_each = local.topic_names
  name     = each.key
}

data "google_cloud_run_v2_service" "api" {
  name     = "visioniq-api"
  location = var.region
}

data "google_cloud_run_v2_service" "intelligence" {
  name     = "visioniq-intelligence"
  location = var.region
}

data "google_service_account" "push" {
  account_id = var.pubsub_push_service_account
}

data "google_service_account" "scheduler" {
  account_id = var.scheduler_service_account
}

# Authenticated push into existing internal Cloud Run services.
# Existing IAM bindings are not destroyed; additive IAM member resources are used.
resource "google_cloud_run_v2_service_iam_member" "pubsub_invoker_api" {
  project  = var.project_id
  location = var.region
  name     = data.google_cloud_run_v2_service.api.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${data.google_service_account.push.email}"
}

resource "google_cloud_run_v2_service_iam_member" "pubsub_invoker_intelligence" {
  project  = var.project_id
  location = var.region
  name     = data.google_cloud_run_v2_service.intelligence.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${data.google_service_account.push.email}"
}

resource "google_service_account_iam_member" "pubsub_oidc_token_creator" {
  service_account_id = data.google_service_account.push.name
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = "serviceAccount:${local.pubsub_service_agent}"
}

resource "google_pubsub_topic_iam_member" "dead_letter_publisher" {
  for_each = local.flows
  topic    = data.google_pubsub_topic.existing[each.value.dead_letter_topic].name
  role     = "roles/pubsub.publisher"
  member   = "serviceAccount:${local.pubsub_service_agent}"
}

resource "google_pubsub_subscription" "dead_letter_hold" {
  for_each = local.flows
  name     = "${each.value.dead_letter_topic}-hold"
  topic    = data.google_pubsub_topic.existing[each.value.dead_letter_topic].id
  project  = var.project_id

  message_retention_duration = "${local.topology.retry.message_retention_seconds}s"
  expiration_policy {
    ttl = ""
  }
}

resource "google_pubsub_subscription" "push" {
  for_each = local.flows
  name     = each.value.subscription
  topic    = data.google_pubsub_topic.existing[each.value.topic].id
  project  = var.project_id

  ack_deadline_seconds       = local.topology.retry.ack_deadline_seconds
  message_retention_duration = "${local.topology.retry.message_retention_seconds}s"

  push_config {
    push_endpoint = "${local.endpoints[each.value.consumer]}${local.topology.push_path}"
    oidc_token {
      service_account_email = data.google_service_account.push.email
      audience              = local.endpoints[each.value.consumer]
    }
  }

  retry_policy {
    minimum_backoff = "${local.topology.retry.minimum_backoff_seconds}s"
    maximum_backoff = "${local.topology.retry.maximum_backoff_seconds}s"
  }

  dead_letter_policy {
    dead_letter_topic     = data.google_pubsub_topic.existing[each.value.dead_letter_topic].id
    max_delivery_attempts = local.topology.retry.max_delivery_attempts
  }

  expiration_policy {
    ttl = ""
  }

  depends_on = [
    google_cloud_run_v2_service_iam_member.pubsub_invoker_api,
    google_cloud_run_v2_service_iam_member.pubsub_invoker_intelligence,
    google_service_account_iam_member.pubsub_oidc_token_creator,
    google_pubsub_topic_iam_member.dead_letter_publisher
  ]
}

resource "google_pubsub_subscription_iam_member" "dead_letter_reader" {
  for_each     = local.flows
  subscription = google_pubsub_subscription.push[each.key].name
  role         = "roles/pubsub.subscriber"
  member       = "serviceAccount:${local.pubsub_service_agent}"
}

# Jobs are definitions deployed by the application's GitLab pipeline.
# Do not create or execute Cloud Run jobs from this Terraform module.
resource "google_cloud_scheduler_job" "run_job" {
  for_each    = local.schedules
  name        = each.value.name
  region      = var.region
  description = "VisionIQ DEV; paused until cloud integration acceptance"
  schedule    = each.value.schedule
  time_zone   = each.value.time_zone
  paused      = true

  attempt_deadline = "180s"

  http_target {
    uri         = "https://run.googleapis.com/v2/projects/${var.project_id}/locations/${var.region}/jobs/${each.value.cloud_run_job}:run"
    http_method = "POST"

    oauth_token {
      service_account_email = data.google_service_account.scheduler.email
      scope                 = "https://www.googleapis.com/auth/cloud-platform"
    }
  }

  depends_on = [
    google_cloud_run_v2_job_iam_member.scheduler_job_invoker
  ]
}

resource "google_cloud_run_v2_job_iam_member" "scheduler_job_invoker" {
  for_each = local.schedules
  project  = var.project_id
  location = var.region
  name     = each.value.cloud_run_job
  role     = "roles/run.invoker"
  member   = "serviceAccount:${data.google_service_account.scheduler.email}"
}
